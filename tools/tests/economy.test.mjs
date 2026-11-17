// Meta-economy engine tests (web-playable/js/economy.js). Run: npm test
// Verifies the server-authoritative / integrity architecture adapted from the Block Out! report.
import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const E = require('../../web-playable/js/economy.js');

const MIN = 60000;
function memStorage() {
  const m = new Map();
  return { getItem: (k) => (m.has(k) ? m.get(k) : null), setItem: (k, v) => m.set(k, String(v)), removeItem: (k) => m.delete(k), _m: m };
}
// Mutable clock so we can advance time deterministically.
function clock(startMs = 1_000_000_000_000) { const c = { t: startMs }; c.now = () => c.t; c.advance = (ms) => (c.t += ms); return c; }
function freshMeta(over = {}) {
  const c = over.clock || clock();
  const storage = over.storage || memStorage();
  return { meta: E.createMeta({ now: c.now, storage, key: 'test.meta' }), c, storage };
}

describe('Lives — DateTime-based regeneration', () => {
  test('livesFromAnchor: full when anchor passed, regenerates one per interval', () => {
    const now = 1000 * MIN, regen = 20 * MIN, max = 5;
    assert.equal(E.livesFromAnchor(0, now, max, regen).lives, 5);
    // anchor 40 min out ⇒ missing 2 ⇒ 3 lives
    assert.equal(E.livesFromAnchor(now + 40 * MIN, now, max, regen).lives, 3);
    // time to next life is the partial remainder of the current interval
    assert.equal(E.livesFromAnchor(now + 25 * MIN, now, max, regen).toNextMs, 5 * MIN);
  });

  test('spending a life pushes the refill anchor forward; it is not a plain int', () => {
    const { meta, c } = freshMeta();
    assert.equal(meta.inventory.lives(), 5);
    assert.equal(meta.inventory.spend('level:start', 'life', 1), true);
    assert.equal(meta.inventory.lives(), 4);
    assert.ok(meta.inventory.secondsToNextLife() > 0 && meta.inventory.secondsToNextLife() <= 20 * 60);
    c.advance(20 * MIN); // one interval later → back to 5
    assert.equal(meta.inventory.lives(), 5);
  });

  test('cannot spend a life you do not have', () => {
    const { meta } = freshMeta();
    for (let i = 0; i < 5; i++) assert.equal(meta.inventory.spend('level:start', 'life', 1), true);
    assert.equal(meta.inventory.lives(), 0);
    assert.equal(meta.inventory.spend('level:start', 'life', 1), false); // backend rejects
  });

  test('Magic Pass grants limitless lives — spending consumes nothing', () => {
    const { meta } = freshMeta();
    assert.equal(meta.shop.buy('magic_pass').ok, true);
    assert.equal(meta.inventory.hasLimitless('life'), true);
    for (let i = 0; i < 10; i++) meta.inventory.spend('level:start', 'life', 1);
    assert.equal(meta.inventory.lives(), 5);
  });
});

describe('Server-authoritative economy', () => {
  test('coin spend is re-validated by the backend', () => {
    const { meta } = freshMeta();
    const start = meta.inventory.coins;
    assert.equal(meta.inventory.spend('shop', 'coin', 40), true);
    assert.equal(meta.inventory.coins, start - 40);
    assert.equal(meta.inventory.spend('shop', 'coin', 10_000), false); // insufficient → rejected
    assert.equal(meta.inventory.coins, start - 40);
  });

  test('a tampered client cache is reconciled from the backend, not trusted', () => {
    const { meta, storage, c } = freshMeta();
    meta.inventory.earn('test', { coin: 0 }); // force a signed save
    // Attacker rewrites the local save with 999999 coins and a stale signature.
    const env = JSON.parse(storage.getItem('test.meta.inv'));
    env.m.coins = 999999;
    storage.setItem('test.meta.inv', JSON.stringify(env)); // signature no longer matches
    const meta2 = E.createMeta({ now: c.now, storage, key: 'test.meta' });
    assert.equal(meta2.inventory.wasTampered(), true);
    assert.ok(meta2.inventory.coins < 999999, 'tampered coins rejected');
  });

  test('cloud sync rejects an implausible coin jump (no last-write-wins overwrite)', () => {
    const { meta } = freshMeta();
    meta.inventory.model.coins += 50_000;            // forged client value
    const r = meta.inventory.sync();
    assert.equal(r.ok, false);
    assert.equal(r.error, 'reconcile_rejected');
  });
});

describe('Boosters — TryUse / Spend with coin fallback', () => {
  test('uses from stock when available', () => {
    const { meta } = freshMeta();
    const before = meta.inventory.boosterCount('xray'); // starts at 2
    const r = meta.boosters.tryUse('xray', true);
    assert.equal(r.action, 'used');
    assert.equal(meta.inventory.boosterCount('xray'), before - 1);
  });

  test('falls back to coins at the shelf price when out of stock', () => {
    const { meta } = freshMeta();
    while (meta.inventory.boosterCount('hammer') > 0) meta.inventory.spend('drain', 'hammer', 1);
    const coins = meta.inventory.coins; // 100 ≥ hammer price 120? no → needCoins
    const r1 = meta.boosters.tryUse('hammer', true);
    assert.equal(r1.action, 'needCoins');
    meta.inventory.earn('grant', { coin: 200 });
    const r2 = meta.boosters.tryUse('hammer', true);
    assert.equal(r2.action, 'bought_with_coins');
    assert.equal(meta.inventory.coins, coins + 200 - E.boosterCoinPrice('hammer'));
  });

  test('game-state gate blocks activation', () => {
    const { meta } = freshMeta();
    assert.equal(meta.boosters.tryUse('xray', false).action, 'notApplicable');
  });
});

describe('Pending buffer', () => {
  test('accrues then applies to the real balance', () => {
    const { meta } = freshMeta();
    const start = meta.inventory.coins;
    meta.inventory.pending({ coin: 30, xray: 1 });
    assert.equal(meta.inventory.coins, start);          // not yet applied
    assert.equal(meta.inventory.hasPending(), true);
    meta.inventory.applyAllPending();
    assert.equal(meta.inventory.coins, start + 30);
    assert.equal(meta.inventory.hasPending(), false);
  });
});

describe('Shop / IAP (dual channel, receipt-validated)', () => {
  test('grants from the backend; one-time offer cannot be re-bought', () => {
    const { meta } = freshMeta();
    const start = meta.inventory.coins;
    const r = meta.shop.buy('starter_pack');       // web channel, once
    assert.equal(r.ok, true);
    assert.equal(meta.inventory.coins, start + 800);
    assert.equal(meta.shop.buy('starter_pack').ok, false); // already owned
  });

  test('coin pack credits coins', () => {
    const { meta } = freshMeta();
    const start = meta.inventory.coins;
    assert.equal(meta.shop.buy('coins_1500').ok, true);
    assert.equal(meta.inventory.coins, start + 1500);
  });

  test('permanent remove-ads survives a reload (no Infinity→null serialization loss)', () => {
    const { meta, c, storage } = freshMeta();
    assert.equal(meta.shop.buy('no_ads').ok, true);
    assert.equal(meta.inventory.hasLimitless('noAds'), true);
    const meta2 = E.createMeta({ now: c.now, storage, key: 'test.meta' }); // reload from the same store
    assert.equal(meta2.inventory.hasLimitless('noAds'), true);
    assert.equal(meta2.shop.available(meta2.shop.product('no_ads')), false); // already owned
  });
});

describe('Daily / Wheel / Arena', () => {
  test('daily reward claims once per day and builds a streak', () => {
    const { meta, c } = freshMeta();
    assert.equal(meta.daily.canClaim(), true);
    const a = meta.daily.claim();
    assert.equal(a.streak, 1);
    assert.equal(meta.daily.canClaim(), false);      // same day
    assert.equal(meta.daily.claim(), null);
    c.advance(24 * 60 * MIN);                         // next day
    const b = meta.daily.claim();
    assert.equal(b.streak, 2);
  });

  test('arena submit keeps the best score and ranks the player', () => {
    const { meta } = freshMeta();
    meta.arena.submit(300);
    meta.arena.submit(120);                           // lower → ignored by server
    assert.equal(meta.arena.myScore(), 300);
    const board = meta.arena.board();
    assert.ok(board.rows.find((r) => r.me).score === 300);
    assert.equal(board.rows.length, meta.config.arena.bots.length + 1);
  });
});
