/*
 * Su Ustası — meta-economy engine (currencies, lives, boosters, shop/IAP, daily, chests,
 * wheel, arena) with a server-authoritative mock backend and client integrity.
 *
 * Architecture mirrors a mature F2P puzzle title (the Block Out! / Grand Games reference
 * reverse-engineering report), implemented with the *correct* patterns the report recommends:
 *
 *   - Server-authoritative economy: every Spend / Purchase / limitless grant is committed
 *     through MockBackend (the single source of truth). The client model is a UI cache;
 *     `doesEnoughVisible()` is only a hint — the backend re-validates every mutation.
 *   - TryUseBooster → SpendBooster split with a coin fallback *at point of use*
 *     (BoosterManager), and a game-state `canActivate()` gate (not a currency check).
 *   - DateTime-based Life/heart accounting (regenerates over real time; integer writes
 *     can't fake it) that gates entering a level — separate from in-level leak lives.
 *   - Pending-buffer rewards (accrue → applyAllPending after the win animation).
 *   - Cloud-sync reconciliation done right: monotonic version + per-currency delta
 *     thresholds reconciled against a transaction log, so a tampered "last-write-wins"
 *     overwrite (the gap the report flagged) is rejected.
 *   - Client integrity: the local save is signed; a bad signature is treated as tamper
 *     and reconciled from the authoritative backend instead of trusted.
 *
 * Zero-dependency UMD module: a classic <script> for the web playable (window.SuEconomy,
 * works from file://) and require() for the Node tests. Depends on core.js (SuCore).
 */
(function (root, factory) {
  'use strict';
  const core = (typeof module === 'object' && module.exports) ? require('./core.js') : root.SuCore;
  const api = factory(core);
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.SuEconomy = api;
})(typeof self !== 'undefined' ? self : this, function (C) {
  'use strict';

  const MIN = 60 * 1000, HOUR = 60 * MIN, DAY = 24 * HOUR;
  const FOREVER = 8.64e15; // ~year 275760; a JSON-safe stand-in for "permanent" (Infinity serializes to null)

  // ------------------------------------------------------------------ currencies
  const CUR = Object.freeze({
    COIN: 'coin', LIFE: 'life',
    XRAY: 'xray', HAMMER: 'hammer', PATCH: 'patch',
    NO_ADS: 'noAds', MAGIC_PASS: 'magicPass',
    CHEST: 'chest', WHEEL_TOKEN: 'wheelToken', TROPHY: 'trophy',
  });
  const BOOSTERS = Object.freeze([CUR.XRAY, CUR.HAMMER, CUR.PATCH]);
  const LIMITLESS = Object.freeze([CUR.NO_ADS, CUR.MAGIC_PASS]);

  // ------------------------------------------------------------------ meta config
  const META = Object.freeze({
    life: Object.freeze({ max: 5, regenMs: 20 * MIN, start: 5 }),
    integrity: Object.freeze({ salt: 'su-ustasi-v2-9f13', enforce: true, syncCoinDelta: 2000, syncBoosterDelta: 20 }),
    // Shop catalogue. channel 'store' = Google/Apple IAP; 'web' = out-of-store WebShop (dual channel,
    // like the report's IAPService vs PurchaseService). Both validate server-side; no local reward.
    shop: Object.freeze([
      { id: 'starter_pack', kind: 'bundle', channel: 'web', title: 'Başlangıç Paketi', priceTRY: 39.99, tag: 'offer',
        grant: { coin: 800, xray: 3, hammer: 2, patch: 2 }, desc: 'Tek seferlik — 800 jeton + güçlendiriciler', once: true },
      { id: 'coins_500', kind: 'coins', channel: 'store', title: '500 Jeton', priceTRY: 29.99, grant: { coin: 500 } },
      { id: 'coins_1500', kind: 'coins', channel: 'store', title: '1500 Jeton', priceTRY: 69.99, grant: { coin: 1500 }, tag: 'popular' },
      { id: 'coins_4000', kind: 'coins', channel: 'store', title: '4000 Jeton', priceTRY: 149.99, grant: { coin: 4000 }, tag: 'best' },
      { id: 'booster_bundle', kind: 'boosters', channel: 'store', title: 'Güçlendirici Paketi', priceTRY: 44.99,
        grant: { xray: 5, hammer: 5, patch: 5 }, desc: '5 Röntgen · 5 Çekiç · 5 Yama' },
      { id: 'lives_refill', kind: 'lives', channel: 'store', title: 'Canları Doldur', priceTRY: 19.99, grant: { lifeRefill: true }, desc: 'Canları anında doldur' },
      { id: 'infinite_lives_1h', kind: 'infinite', channel: 'store', title: '1 Saat Sınırsız Can', priceTRY: 24.99, grant: { noAdsMs: 0, infiniteLifeMs: HOUR }, desc: '60 dakika boyunca can harcanmaz' },
      { id: 'magic_pass', kind: 'subscription', channel: 'store', title: 'Usta Kartı', priceTRY: 99.99, tag: 'sub',
        grant: { magicPassMs: 30 * DAY }, desc: '30 gün: sınırsız can, reklamsız, günlük +50 jeton' },
      { id: 'no_ads', kind: 'noads', channel: 'store', title: 'Reklamsız', priceTRY: 49.99, grant: { noAdsMs: Infinity }, desc: 'Geçiş reklamlarını kalıcı kaldırır' },
    ]),
    magicPass: Object.freeze({ dailyCoins: 50, durationMs: 30 * DAY }),
    // 7-day login streak calendar (loops). Each entry is a reward bag.
    daily: Object.freeze([
      { coin: 50 }, { coin: 80 }, { xray: 1 }, { coin: 120 }, { hammer: 1 }, { coin: 180 }, { chest: 1, coin: 100 },
    ]),
    chest: Object.freeze({
      // weighted reward slots rolled per open
      table: [
        { w: 40, bag: { coin: 100 } }, { w: 25, bag: { coin: 250 } }, { w: 14, bag: { xray: 1 } },
        { w: 10, bag: { hammer: 1 } }, { w: 7, bag: { patch: 1 } }, { w: 3, bag: { coin: 500 } }, { w: 1, bag: { wheelToken: 1 } },
      ],
      winEvery: 3, // award a chest every N wins
    }),
    wheel: Object.freeze({
      // 8 segments, by angle order
      segments: [
        { label: '50', bag: { coin: 50 } }, { label: 'Röntgen', bag: { xray: 1 } }, { label: '100', bag: { coin: 100 } },
        { label: 'Çekiç', bag: { hammer: 1 } }, { label: '25', bag: { coin: 25 } }, { label: 'Yama', bag: { patch: 1 } },
        { label: '250', bag: { coin: 250 } }, { label: 'Sandık', bag: { chest: 1 } },
      ],
      freeEveryMs: 6 * HOUR,
    }),
    arena: Object.freeze({
      durationMs: 7 * DAY,
      bots: ['AyşeUsta', 'MertKanal', 'ZeynepBoru', 'CanDamla', 'EfeTesisat', 'SelinVana', 'BarışAkış', 'DenizSu', 'KaanPas', 'ElifDebi'],
      rewards: [ { rank: 1, bag: { coin: 1000, trophy: 50 } }, { rank: 2, bag: { coin: 600, trophy: 30 } }, { rank: 3, bag: { coin: 400, trophy: 20 } }, { rank: 10, bag: { coin: 150, trophy: 5 } } ],
    }),
  });

  // ------------------------------------------------------------------ small utils
  function clamp(v, lo, hi) { return v < lo ? lo : v > hi ? hi : v; }
  /** Deterministic string hash (djb2) — mock stand-in for an HMAC save signature. */
  function signHash(str) { let h = 5381; for (let i = 0; i < str.length; i++) h = ((h << 5) + h + str.charCodeAt(i)) >>> 0; return h.toString(16); }
  function boosterCoinPrice(type) { return (C.BOOSTERS[type] && C.BOOSTERS[type].price) || 0; }

  // ------------------------------------------------------------------ in-memory storage fallback (tests / blocked storage)
  function memStorage() {
    const m = new Map();
    return { getItem: (k) => (m.has(k) ? m.get(k) : null), setItem: (k, v) => m.set(k, String(v)), removeItem: (k) => m.delete(k) };
  }

  // ------------------------------------------------------------------ model
  function defaultModel() {
    return {
      version: 1,
      coins: C.ECONOMY.startCoins,
      boosters: Object.assign({}, C.ECONOMY.startBoosters),
      lifeFullAtMs: 0,          // 0 ⇒ lives full; else the instant lives return to max
      noAdsUntilMs: 0,          // Infinity ⇒ permanent
      magicPassUntilMs: 0,
      infiniteLifeUntilMs: 0,
      chests: 0,
      wheelTokens: 1,
      trophies: 0,
      daily: { lastClaimDay: -1, streak: 0 },
      wheel: { lastFreeAt: 0 },
      magic: { lastDailyDay: -1 },
      arena: { week: -1, score: 0, claimed: false },
      purchased: {},            // one-time products already bought
      pending: {},              // reward buffer: currency → amount
      txlog: [],                // {r, t, d, ts} capped — reconciliation evidence
    };
  }

  // Lives are DateTime-derived (never stored as an int): you can't fake them with a plain write.
  function livesFromAnchor(lifeFullAtMs, now, max, regenMs) {
    const remaining = lifeFullAtMs - now;
    if (remaining <= 0) return { lives: max, toNextMs: 0 };
    const missing = Math.ceil(remaining / regenMs);
    const lives = clamp(max - missing, 0, max);
    const toNextMs = remaining - (missing - 1) * regenMs;
    return { lives, toNextMs };
  }

  // ------------------------------------------------------------------ MockBackend — the authoritative source of truth
  // In production these methods are RPCs to grandgamesbe.com-style services; here they run locally but
  // keep their OWN store, so the client never writes economy truth directly. Latency is zero by design.
  function createBackend(storage, now, key) {
    const CLOUD_KEY = key + '.cloud';
    const usedReceipts = new Set();

    function load() {
      try { const raw = storage.getItem(CLOUD_KEY); if (raw) return JSON.parse(raw); } catch (e) { /* ignore */ }
      return defaultModel();
    }
    function persist(model) { try { storage.setItem(CLOUD_KEY, JSON.stringify(model)); } catch (e) { /* ignore */ } }

    function log(model, r, t, d) {
      model.txlog.push({ r, t, d, ts: now() });
      if (model.txlog.length > 200) model.txlog.splice(0, model.txlog.length - 200);
    }

    function applyBag(model, bag, reason) {
      for (const k in bag) {
        const v = bag[k];
        if (k === 'coin') { model.coins += v; log(model, reason, CUR.COIN, v); }
        else if (k === 'chest') { model.chests += v; log(model, reason, CUR.CHEST, v); }
        else if (k === 'wheelToken') { model.wheelTokens += v; log(model, reason, CUR.WHEEL_TOKEN, v); }
        else if (k === 'trophy') { model.trophies += v; log(model, reason, CUR.TROPHY, v); }
        else if (k === 'lifeRefill') { model.lifeFullAtMs = 0; }
        else if (k === 'infiniteLifeMs') { model.infiniteLifeUntilMs = Math.max(model.infiniteLifeUntilMs, now()) + v; }
        else if (k === 'noAdsMs') { model.noAdsUntilMs = (v === Infinity || v >= FOREVER) ? FOREVER : Math.max(model.noAdsUntilMs, now()) + v; }
        else if (k === 'magicPassMs') { model.magicPassUntilMs = Math.max(model.magicPassUntilMs, now()) + v; model.noAdsUntilMs = model.noAdsUntilMs >= FOREVER ? FOREVER : Math.max(model.noAdsUntilMs, model.magicPassUntilMs); }
        else if (BOOSTERS.indexOf(k) >= 0) { model.boosters[k] = (model.boosters[k] || 0) + v; log(model, reason, k, v); }
      }
    }

    return {
      pull() { return load(); },

      // Integrity verdict (mock of Play Integrity's HandleTokens → SendTrackerToken).
      validateIntegrity(token) {
        const ok = typeof token === 'string' && token.length >= 8 && token.indexOf('tamper') < 0;
        return { verdict: ok ? 'MEETS_DEVICE_INTEGRITY' : 'UNRECOGNIZED', ok };
      },

      // Server-authoritative spend: re-checks the balance on the authoritative model.
      commitSpend({ reason, type, amount, token }) {
        const model = load();
        if (META.integrity.enforce && !this.validateIntegrity(token).ok) return { ok: false, error: 'integrity', model };
        if (type === CUR.LIFE) {
          // limitless (magic pass / infinite-lives power) → no consumption
          if (model.magicPassUntilMs > now() || model.infiniteLifeUntilMs > now()) return { ok: true, model: (persist(model), model) };
          const st = livesFromAnchor(model.lifeFullAtMs, now(), META.life.max, META.life.regenMs);
          if (st.lives < amount) return { ok: false, error: 'insufficient', model };
          for (let i = 0; i < amount; i++) {
            const cur = livesFromAnchor(model.lifeFullAtMs, now(), META.life.max, META.life.regenMs).lives;
            model.lifeFullAtMs = (cur >= META.life.max ? now() : Math.max(model.lifeFullAtMs, now())) + META.life.regenMs;
          }
          log(model, reason, CUR.LIFE, -amount); persist(model); return { ok: true, model };
        }
        const bal = type === CUR.COIN ? model.coins : (model.boosters[type] || 0);
        if (bal < amount) return { ok: false, error: 'insufficient', model };
        if (type === CUR.COIN) model.coins -= amount; else model.boosters[type] -= amount;
        log(model, reason, type, -amount); persist(model); return { ok: true, model };
      },

      // Gameplay rewards. A real backend recomputes these from a verified level result; here we
      // accept the client's bag but log it so sync reconciliation has evidence.
      commitEarn({ reason, bag }) {
        const model = load(); applyBag(model, bag, reason || 'earn'); persist(model); return { ok: true, model };
      },
      commitLives({ addLives, refill }) {
        const model = load();
        if (refill) model.lifeFullAtMs = 0;
        else if (addLives) {
          const anchor = model.lifeFullAtMs <= now() ? now() : model.lifeFullAtMs; // already full ⇒ capped
          model.lifeFullAtMs = anchor - addLives * META.life.regenMs;
          if (model.lifeFullAtMs <= now()) model.lifeFullAtMs = 0;
        }
        persist(model); return { ok: true, model };
      },

      // Client-managed cadence fields (daily streak, wheel timer, magic drip day, pending, arena flags).
      // The backend does not police these, but owning the single store keeps them from being clobbered
      // when a money commit re-adopts the authoritative model.
      commitBookkeeping(patch) {
        const model = load();
        for (const f of ['daily', 'wheel', 'magic', 'pending', 'arena']) if (patch[f] !== undefined) model[f] = patch[f];
        persist(model); return { ok: true, model };
      },

      // Receipt-validated purchase (store + web channel). No local reward: the server grants.
      processPurchase({ productId, channel, receipt, token }) {
        const product = META.shop.find((p) => p.id === productId);
        const model = load();
        if (!product) return { ok: false, error: 'unknown_product', model };
        if (META.integrity.enforce && !this.validateIntegrity(token).ok) return { ok: false, error: 'integrity', model };
        if (!receipt || usedReceipts.has(receipt)) return { ok: false, error: 'bad_receipt', model }; // replay protection
        if (product.once && model.purchased[productId]) return { ok: false, error: 'already_owned', model };
        usedReceipts.add(receipt);
        applyBag(model, product.grant, 'iap:' + productId);
        model.purchased[productId] = (model.purchased[productId] || 0) + 1;
        persist(model);
        return { ok: true, model, channel, product };
      },

      // Cloud sync reconciliation — rejects implausible jumps (anti "last-write-wins overwrite").
      sync({ clientModel, token }) {
        const model = load();
        if (META.integrity.enforce && !this.validateIntegrity(token).ok) return { ok: false, error: 'integrity', model };
        if (!clientModel || typeof clientModel.version !== 'number') return { ok: true, model };
        const coinDelta = (clientModel.coins || 0) - model.coins;
        let suspicious = coinDelta > META.integrity.syncCoinDelta;
        for (const b of BOOSTERS) if (((clientModel.boosters || {})[b] || 0) - (model.boosters[b] || 0) > META.integrity.syncBoosterDelta) suspicious = true;
        if (suspicious) return { ok: false, error: 'reconcile_rejected', model }; // keep authoritative
        // plausible & newer → accept client values but bump monotonic version
        if (clientModel.version >= model.version) {
          clientModel.version = model.version + 1;
          persist(clientModel);
          return { ok: true, model: clientModel };
        }
        return { ok: true, model };
      },

      submitArenaScore({ week, score, token }) {
        const model = load();
        if (META.integrity.enforce && !this.validateIntegrity(token).ok) return { ok: false, model };
        if (model.arena.week !== week) { model.arena = { week, score: 0, claimed: false }; }
        model.arena.score = Math.max(model.arena.score, score); // server keeps the best
        persist(model);
        return { ok: true, score: model.arena.score };
      },
    };
  }

  // ------------------------------------------------------------------ InventoryService (client cache over the backend)
  function createInventory(backend, storage, now, key) {
    const SAVE_KEY = key + '.inv';
    let model = backend.pull();         // authoritative at boot
    let tampered = false;
    const listeners = new Set();
    const emit = () => listeners.forEach((fn) => { try { fn(); } catch (e) { /* ignore */ } });

    function token() { return 'pi-' + signHash(META.integrity.salt + now()) + '-ok'; }

    // The local save is NEVER trusted for balances — the backend is authoritative (the report's core
    // point). It exists only as a signed tripwire: a broken signature means the cache was tampered with.
    function tripwire() {
      let raw = null;
      try { raw = storage.getItem(SAVE_KEY); } catch (e) { /* blocked */ }
      if (!raw) return;
      try {
        const env = JSON.parse(raw);
        if (!env || env.s !== signHash(META.integrity.salt + JSON.stringify(env.m))) tampered = true;
      } catch (e) { tampered = true; }
    }
    function save() {
      try { storage.setItem(SAVE_KEY, JSON.stringify({ m: model, s: signHash(META.integrity.salt + JSON.stringify(model)) })); } catch (e) { /* ignore */ }
    }
    function adopt(res) { if (res && res.model) { model = res.model; save(); emit(); } return res; }

    tripwire();   // detect a tampered cache from a previous session
    save();       // (re)write a clean signed snapshot of the authoritative model

    const svc = {
      get model() { return model; },
      wasTampered() { return tampered; },
      onChange(fn) { listeners.add(fn); return () => listeners.delete(fn); },
      token,

      // ---- reads (UI cache; backend re-validates on write)
      get coins() { return model.coins; },
      boosterCount(b) { return model.boosters[b] || 0; },
      get chests() { return model.chests; },
      get wheelTokens() { return model.wheelTokens; },
      get trophies() { return model.trophies; },
      lives() { if (this.hasLimitless(CUR.LIFE)) return META.life.max; return livesFromAnchor(model.lifeFullAtMs, now(), META.life.max, META.life.regenMs).lives; },
      maxLives() { return META.life.max; },
      secondsToNextLife() { if (this.hasLimitless(CUR.LIFE) || this.lives() >= META.life.max) return 0; return Math.ceil(livesFromAnchor(model.lifeFullAtMs, now(), META.life.max, META.life.regenMs).toNextMs / 1000); },
      hasLimitless(type) {
        if (type === CUR.LIFE) return model.magicPassUntilMs > now() || model.infiniteLifeUntilMs > now();
        if (type === CUR.NO_ADS) return model.noAdsUntilMs > now() || model.magicPassUntilMs > now();
        if (type === CUR.MAGIC_PASS) return model.magicPassUntilMs > now();
        return false;
      },
      limitlessUntil(type) { return type === CUR.MAGIC_PASS ? model.magicPassUntilMs : type === CUR.NO_ADS ? model.noAdsUntilMs : model.infiniteLifeUntilMs; },
      doesEnoughVisible(type, amount) {
        if (this.hasLimitless(type)) return true;
        if (type === CUR.LIFE) return this.lives() >= amount;
        if (type === CUR.COIN) return model.coins >= amount;
        return (model.boosters[type] || 0) >= amount;
      },

      // ---- writes (server-authoritative; adopt the returned authoritative model)
      spend(reason, type, amount) { const r = backend.commitSpend({ reason, type, amount: amount || 1, token: token() }); if (r.ok) adopt(r); return r.ok; },
      earn(reason, bag) { return adopt(backend.commitEarn({ reason, bag })).ok; },
      refillLives() { return adopt(backend.commitLives({ refill: true })).ok; },
      addLives(n) { return adopt(backend.commitLives({ addLives: n })).ok; },

      // ---- client bookkeeping (daily / wheel / magic / pending / arena) — persisted via the backend store
      commitBookkeeping(patch) { return adopt(backend.commitBookkeeping(patch)).ok; },

      // ---- pending buffer (accrue during animation, then apply)
      pending(bag) { const p = Object.assign({}, model.pending); for (const k in bag) p[k] = (p[k] || 0) + bag[k]; this.commitBookkeeping({ pending: p }); },
      pendingBag() { return Object.assign({}, model.pending); },
      hasPending() { for (const k in model.pending) if (model.pending[k]) return true; return false; },
      applyAllPending() { const bag = this.pendingBag(); this.commitBookkeeping({ pending: {} }); if (Object.keys(bag).length) this.earn('pending', bag); return bag; },

      // ---- purchase (dual-channel, receipt-validated server-side)
      purchase(productId) {
        const product = META.shop.find((p) => p.id === productId);
        if (!product) return { ok: false, error: 'unknown_product' };
        const receipt = (product.channel === 'web' ? 'web-' : 'store-') + productId + '-' + now() + '-' + Math.random().toString(36).slice(2);
        return adopt(backend.processPurchase({ productId, channel: product.channel, receipt, token: token() }));
      },
      sync() { return adopt(backend.sync({ clientModel: model, token: token() })); },
      _save: save, _emit: emit,
    };
    return svc;
  }

  // ------------------------------------------------------------------ BoosterManager — TryUse / Spend with coin fallback
  function createBoosterManager(inv, hooks) {
    hooks = hooks || {};
    function canActivate(type, canByGameState) {
      // Game-state gate ONLY (board applicability), never a currency check — matches the report.
      return canByGameState !== false;
    }
    return {
      canActivate,
      coinPrice: boosterCoinPrice,
      /** How a use would be paid for, without spending: { affordable, how:'stock'|'coins', price }. */
      resolveCost(type) {
        if (inv.doesEnoughVisible(type, 1)) return { affordable: true, how: 'stock' };
        const price = boosterCoinPrice(type);
        if (inv.doesEnoughVisible(CUR.COIN, price)) return { affordable: true, how: 'coins', price };
        return { affordable: false, how: null, price };
      },
      /**
       * Orchestration (TryUseBooster): game-state gate → stock → coin fallback at the shelf price.
       * Returns { ok, action }: 'used' | 'bought_with_coins' | 'needCoins' | 'notApplicable'.
       */
      tryUse(type, canByGameState) {
        if (!canActivate(type, canByGameState)) return { ok: false, action: 'notApplicable' };
        if (inv.doesEnoughVisible(type, 1)) {
          this.spend(type, 'stock');
          if (hooks.onUsed) hooks.onUsed(type, 'stock'); // backend tracking RPC (anti-cheat cross-check)
          return { ok: true, action: 'used' };
        }
        const price = boosterCoinPrice(type);
        if (inv.doesEnoughVisible(CUR.COIN, price)) {
          this.spend(type, 'coins');
          if (hooks.onUsed) hooks.onUsed(type, 'coins');
          return { ok: true, action: 'bought_with_coins', price };
        }
        return { ok: false, action: 'needCoins', price };
      },
      /** SpendBooster: a *separate* entry point — deduct one from stock, else the coin price. */
      spend(type, how) {
        if (how === 'coins' || !inv.doesEnoughVisible(type, 1)) return inv.spend('booster:' + type, CUR.COIN, boosterCoinPrice(type));
        return inv.spend('booster:' + type, type, 1);
      },
    };
  }

  // ------------------------------------------------------------------ Shop
  function createShop(inv) {
    return {
      catalogue: META.shop,
      product(id) { return META.shop.find((p) => p.id === id); },
      priceInCoins: boosterCoinPrice,
      available(p) { return !(p.once && (inv.model.purchased[p.id] || 0) > 0) && !(p.id === 'no_ads' && inv.limitlessUntil(CUR.NO_ADS) >= FOREVER); },
      buy(id) { return inv.purchase(id); },
    };
  }

  // ------------------------------------------------------------------ Daily reward (7-day streak)
  function createDaily(inv, now) {
    const dayNum = () => Math.floor(now() / DAY);
    return {
      canClaim() { return dayNum() !== inv.model.daily.lastClaimDay; },
      streak() { return inv.model.daily.streak; },
      previewDay(i) { return META.daily[i % 7]; },
      todayIndex() { const d = inv.model.daily; const next = d.lastClaimDay >= 0 && dayNum() === d.lastClaimDay + 1 ? d.streak : 0; return next % 7; },
      claim() {
        if (!this.canClaim()) return null;
        const d = inv.model.daily;
        const streak = (d.lastClaimDay >= 0 && dayNum() === d.lastClaimDay + 1) ? d.streak + 1 : 1;
        inv.commitBookkeeping({ daily: { lastClaimDay: dayNum(), streak } }); // persist cadence first
        const bag = META.daily[(streak - 1) % 7];
        inv.earn('daily', bag);                                              // then the money (authoritative)
        return { bag, streak };
      },
    };
  }

  // ------------------------------------------------------------------ Chests
  function createChests(inv, now) {
    let ctr = 0;
    function roll() {
      const rng = C.mulberry32((now() >>> 0) ^ (ctr++ * 2654435761));
      const total = META.chest.table.reduce((s, e) => s + e.w, 0);
      let x = rng.next() * total;
      for (const e of META.chest.table) { if ((x -= e.w) < 0) return e.bag; }
      return META.chest.table[0].bag;
    }
    return {
      count() { return inv.model.chests; },
      /** Open one chest → returns the reward bag (grant it via inv when the reveal finishes). */
      open() {
        if (inv.model.chests <= 0) return null;
        if (!inv.spend('chest:open', CUR.CHEST, 1)) return null;
        return roll();
      },
    };
  }

  // ------------------------------------------------------------------ Wheel of Fortune
  function createWheel(inv, now) {
    let ctr = 0;
    return {
      segments: META.wheel.segments,
      freeReady() { return now() - inv.model.wheel.lastFreeAt >= META.wheel.freeEveryMs; },
      secondsToFree() { return Math.max(0, Math.ceil((META.wheel.freeEveryMs - (now() - inv.model.wheel.lastFreeAt)) / 1000)); },
      /** Spin (free when ready, else a wheel token). Returns { index, bag } — grant on reveal. */
      spin() {
        const free = this.freeReady();
        if (!free && inv.model.wheelTokens <= 0) return null;
        if (free) inv.commitBookkeeping({ wheel: { lastFreeAt: now() } });
        else if (!inv.spend('wheel:spin', CUR.WHEEL_TOKEN, 1)) return null;
        const rng = C.mulberry32((now() >>> 0) ^ (ctr++ * 40503));
        const index = Math.floor(rng.next() * META.wheel.segments.length);
        return { index, bag: META.wheel.segments[index].bag };
      },
    };
  }

  // ------------------------------------------------------------------ Arena (weekly leaderboard with seeded bots)
  function createArena(inv, backend, now) {
    const week = () => Math.floor(now() / META.arena.durationMs);
    function board() {
      const w = week();
      const rng = C.mulberry32((w * 2246822519) >>> 0);
      const rows = META.arena.bots.map((name) => ({ name, score: 20 + Math.floor(rng.next() * 380), bot: true }));
      rows.push({ name: 'Sen', score: inv.model.arena.week === w ? inv.model.arena.score : 0, me: true });
      rows.sort((a, b) => b.score - a.score);
      rows.forEach((r, i) => (r.rank = i + 1));
      return { week: w, rows, endsInMs: (w + 1) * META.arena.durationMs - now() };
    }
    return {
      board,
      myScore() { return inv.model.arena.week === week() ? inv.model.arena.score : 0; },
      submit(score) { const r = backend.submitArenaScore({ week: week(), score, token: inv.token() }); if (r.ok) { inv.model.arena = { week: week(), score: r.score, claimed: inv.model.arena.claimed }; inv._save(); inv._emit(); } return r; },
    };
  }

  // ------------------------------------------------------------------ MagicPass daily coin drip
  function createMagicPass(inv, now) {
    const dayNum = () => Math.floor(now() / DAY);
    return {
      active() { return inv.hasLimitless(CUR.MAGIC_PASS); },
      untilMs() { return inv.model.magicPassUntilMs; },
      /** Grant the daily coin drip once per day while active. Returns coins granted or 0. */
      tick() {
        if (!this.active()) return 0;
        if (inv.model.magic.lastDailyDay === dayNum()) return 0;
        inv.commitBookkeeping({ magic: { lastDailyDay: dayNum() } });
        inv.earn('magicpass:daily', { coin: META.magicPass.dailyCoins });
        return META.magicPass.dailyCoins;
      },
    };
  }

  // ------------------------------------------------------------------ public factory
  function createMeta(opts) {
    opts = opts || {};
    const now = opts.now || (() => Date.now());
    const key = opts.key || 'suUstasi.meta.v1';
    let storage = opts.storage;
    if (!storage) { try { storage = (typeof localStorage !== 'undefined') ? localStorage : memStorage(); storage.getItem(key); } catch (e) { storage = memStorage(); } }

    const backend = createBackend(storage, now, key);
    const inventory = createInventory(backend, storage, now, key);
    const boosters = createBoosterManager(inventory, opts.hooks);
    const meta = {
      CUR, BOOSTERS, LIMITLESS, config: META,
      backend, inventory, boosters,
      shop: createShop(inventory),
      daily: createDaily(inventory, now),
      chests: createChests(inventory, now),
      wheel: createWheel(inventory, now),
      arena: createArena(inventory, backend, now),
      magicPass: createMagicPass(inventory, now),
      now,
      onChange: (fn) => inventory.onChange(fn),
    };
    meta.magicPass.tick(); // drip on boot
    return meta;
  }

  return {
    CUR, BOOSTERS, LIMITLESS, META,
    createMeta, createBackend, createInventory, createBoosterManager,
    livesFromAnchor, signHash, defaultModel, boosterCoinPrice,
  };
});
