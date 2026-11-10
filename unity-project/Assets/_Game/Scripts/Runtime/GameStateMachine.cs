using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SuUstasi.Core;
using SuUstasi.Gameplay;
using SuUstasi.Services;
using UnityEngine;

namespace SuUstasi.Runtime
{
    public enum GameState
    {
        Boot,
        Playing,
        Hammer,
        Paused,
        Won,
        Lost,
    }

    /// <summary>
    /// Game flow conductor: level loading, input → rules (GameSession) → view events, boosters,
    /// win/lose screens with the Usta mascot, rewards, ad pacing, FTUE and mechanic intros.
    /// All rules live in pure C# (Core/Gameplay); this class only orchestrates.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class GameStateMachine : MonoBehaviour
    {
        [Tooltip("0 = continue from the save file; otherwise start at this level number.")]
        public int startLevelNumber;

        public static GameStateMachine Instance { get; private set; }

        private IAnalyticsService _analytics;
        private IAdsService _ads;
        private ISaveService _save;
        private IIapService _iap;
        private IClock _clock;
        private EconomyService _economy;
        private BoosterController _boosters;
        private InterstitialGate _gate;
        private LevelRepository _repo;
        private BoardView _board;
        private HudView _hud;
        private SfxPlayer _sfx;
        private Camera _cam;
        private StateMachine<GameState> _fsm;
        private GameState _resume = GameState.Playing;

        private GameSession _session;
        private int _index;
        private double _startedAt;
        private bool _revived;
        private string _hand;
        private Vector2Int _screen;
        private readonly HashSet<string> _toasted = new HashSet<string>();
        private readonly List<Coroutine> _levelRoutines = new List<Coroutine>();

        public GameSession Session => _session;
        public GameState State => _fsm.Current;

        // ------------------------------------------------------------------ lifecycle
        private void Awake()
        {
            Instance = this;
            Application.targetFrameRate = 60;
            var log = new UnityLogSink();
            _clock = ServiceLocator.Get<IClock>() ?? new UnityClock();
            _analytics = ServiceLocator.Get<IAnalyticsService>() ?? new LocalAnalyticsService(log);
            _save = ServiceLocator.Get<ISaveService>() ?? new JsonFileSaveService(Path.Combine(Application.persistentDataPath, "su_ustasi_save.json"), log);
            _iap = ServiceLocator.Get<IIapService>() ?? new MockIapService();

            _cam = Camera.main;
            if (_cam == null)
            {
                var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                _cam = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
            }
            _cam.orthographic = true;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = PaletteColors.Hex(Palette.Background);
            _cam.transform.position = new Vector3(0, 0, -10);

            PointerInput.EnsureEventSystem();
            _hud = new GameObject("HUD", typeof(RectTransform)).AddComponent<HudView>();
            _hud.Build();
            _hud.BoosterClicked += OnBooster;
            _hud.SettingsClicked += ShowSettings;
            _hud.CoinsClicked += ShowShop;
            _ads = ServiceLocator.Get<IAdsService>() ?? new OverlayMockAdsService(_hud, this);

            _economy = new EconomyService(_save, _analytics, _iap);
            _economy.Changed += RefreshHud;
            _boosters = new BoosterController(_economy, _analytics, _clock);
            _gate = new InterstitialGate(new AdPolicy(), _ads, _analytics, _clock, () => _economy.Progress.NoAds);
            _repo = new LevelRepository();
            _board = new GameObject("Board").AddComponent<BoardView>();
            _sfx = gameObject.AddComponent<SfxPlayer>();
            _sfx.Enabled = _economy.Progress.SoundOn;

            _fsm = new StateMachine<GameState>(GameState.Boot)
                .Allow(GameState.Boot, GameState.Playing, GameState.Paused)
                .Allow(GameState.Playing, GameState.Playing, GameState.Hammer, GameState.Paused, GameState.Won, GameState.Lost)
                .Allow(GameState.Hammer, GameState.Playing, GameState.Paused, GameState.Won, GameState.Lost)
                .Allow(GameState.Paused, GameState.Playing, GameState.Hammer, GameState.Paused, GameState.Won, GameState.Lost)
                .Allow(GameState.Won, GameState.Playing, GameState.Paused, GameState.Won)
                .Allow(GameState.Lost, GameState.Playing, GameState.Paused, GameState.Lost);
            _fsm.Changed += (from, to) => RefreshHud();
        }

        private void Start()
        {
            if (_repo.Count == 0)
            {
                _hud.ShowModal(null, "Seviye yok", "Resources/Levels boş. Çalıştır: node tools/generate_levels.mjs\nveya Su Ustası ▸ Level Generator", null);
                return;
            }
            int start = Mathf.Clamp(_economy.Progress.MaxLevelIndex, 0, _repo.Count - 1);
            if (startLevelNumber > 0 && _repo.IndexOfLevel(startLevelNumber) >= 0) start = _repo.IndexOfLevel(startLevelNumber);
            StartLevel(start);
        }

        public void StartLevel(int index)
        {
            foreach (var r in _levelRoutines)
                if (r != null) StopCoroutine(r);
            _levelRoutines.Clear();
            _index = Mathf.Clamp(index, 0, _repo.Count - 1);
            LevelData data;
            try
            {
                data = _repo.Load(_index);
            }
            catch (Exception e)
            {
                _hud.Toast("Seviye yüklenemedi: " + e.Message, 5f);
                return;
            }
            _session = new GameSession(data);
            _boosters.ResetForLevel();
            _revived = false;
            _hand = null;
            _startedAt = _clock.Now;
            _board.Build(_session, BoardTheme.For(Progression.ThemeFor(data.Level)));
            FitCamera();
            _hud.CloseModal();
            _hud.HideTooltip();
            _fsm.Go(GameState.Playing);
            _analytics.TrackLevelStart(data.Level, data.Pipes.Count, Progression.ThemeFor(data.Level));

            var intro = MechanicIntroCatalog.For(data);
            if (intro != null && !_economy.Progress.SeenIntros.Contains(intro.Type.ToJson())) ShowIntro(intro);
            else AfterIntro();
        }

        private void AfterIntro()
        {
            int level = _session.LevelNumber;
            foreach (var b in BoosterRules.All)
                if (_economy.GiftOnUnlock(b, level)) _hud.Toast("Yeni güçlendirici açıldı: " + BoosterName(b) + "! 1 tane hediye.", 4f);
            StartFtue(level);
            RefreshHud();
        }

        private void Update()
        {
            if (_session == null) return;
            if (Screen.width != _screen.x || Screen.height != _screen.y) FitCamera();
            _board.Tank?.SetLevel(_session.WaterRatio);

            bool xray = _boosters.IsXRayActive;
            foreach (var v in _board.Pipes)
            {
                v.XRay = xray && _session.WouldExitOnTap(v.Pipe);
                v.Pending = v.Pipe.PendingExit;
            }

            if (PointerInput.KeyDown(KeyCode.R) && !_hud.ModalOpen) StartLevel(_index);
            if (PointerInput.KeyDown(KeyCode.M)) ToggleSound();
            if (PointerInput.KeyDown(KeyCode.Escape) && _fsm.Current == GameState.Hammer) CancelHammer();

            if ((_fsm.Current == GameState.Playing || _fsm.Current == GameState.Hammer) && !_hud.ModalOpen && PointerInput.TryGetPress(out var screen) && !PointerInput.IsOverUi(screen))
            {
                var world = _cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -_cam.transform.position.z));
                if (_board.TryGetCell(world, out var cell))
                {
                    var pipe = _session.Board.PipeAt(cell);
                    if (pipe != null)
                    {
                        if (_fsm.Current == GameState.Hammer) UseHammer(pipe);
                        else HandleTap(pipe);
                    }
                }
            }
        }

        private void FitCamera()
        {
            _screen = new Vector2Int(Screen.width, Screen.height);
            if (_session == null) return;
            float aspect = (float)Screen.width / Mathf.Max(1, Screen.height);
            const float top = 0.17f, bottom = 0.2f; // HUD bands (fraction of screen height)
            float w = _session.Board.Width + 0.8f, h = _session.Board.Height + 0.8f;
            float size = Mathf.Max(h / 2f / (1 - top - bottom), w / aspect / 2f);
            _cam.orthographicSize = size;
            float centre = (bottom + (1 - top)) / 2f; // viewport y of the free band's centre
            _cam.transform.position = new Vector3(0, -(centre - 0.5f) * 2f * size, -10);
        }

        // ------------------------------------------------------------------ taps
        private void HandleTap(Pipe pipe)
        {
            var view = _board.ViewFor(pipe.Id);
            var res = _session.Tap(pipe.Id);
            switch (res.Kind)
            {
                case TapKind.Ignored:
                    return;
                case TapKind.Exit:
                    if (_hand == pipe.Id)
                    {
                        _hand = null;
                        _board.ShowHand(null);
                        FtueStep("l1_first_exit");
                        _hud.Tooltip("Harika! Depodaki su azalıyor. Hepsini boşalt!", false);
                        Later(3f, _hud.HideTooltip);
                    }
                    break;
                case TapKind.Blocked:
                    view.PlayShake();
                    foreach (var id in res.BlockingPipeIds) _board.ViewFor(id)?.PlayFlash();
                    _board.ShowSweep(res.SweptCells);
                    _sfx.Play(Sfx.Bonk);
                    _analytics.TrackBlockedTap(_session.LevelNumber, pipe.Id, "blocked", res.LeakCost);
                    if (_session.LevelNumber == 2 && FtueStep("l2_blocked_tap"))
                    {
                        _hud.Tooltip("Takıldı! Önündeki boru yolu kapatıyor. Normalde bu 1 damlaya mal olur.", false);
                        Later(3.8f, _hud.HideTooltip);
                    }
                    break;
                case TapKind.Locked:
                    view.PlayShake();
                    foreach (var k in _session.Board.Pipes)
                        if (k.Type == PipeType.KeyValve && k.TargetId == pipe.Id && !k.Removed) _board.ViewFor(k.Id)?.PlayFlash();
                    _sfx.Play(Sfx.Bonk);
                    _analytics.TrackBlockedTap(_session.LevelNumber, pipe.Id, "locked", res.LeakCost);
                    ToastOnce("locked", "Zincirli! Önce aynı renkli anahtar vanalı boruyu çıkar.");
                    break;
                case TapKind.Frozen:
                    view.PlayShake();
                    view.PlayBadgePop();
                    _board.Burst(pipe, Color.white, 10, 0.6f);
                    _sfx.Play(Sfx.Ice);
                    _sfx.Play(Sfx.Bonk, 0.7f);
                    _analytics.TrackBlockedTap(_session.LevelNumber, pipe.Id, "frozen", res.LeakCost);
                    ToastOnce("frozen", "Donmuş! " + pipe.IceCount + " boru daha çıkınca buz çözülecek.");
                    break;
                case TapKind.RustBroken:
                    _board.Burst(pipe, PaletteColors.Hex(Palette.Rusty), 16, 0.9f);
                    view.Rebuild();
                    _sfx.Play(Sfx.Crack);
                    ToastOnce("rusty", "Pas kırıldı! Yol açıksa bir dokunuşla kayar.");
                    break;
                case TapKind.ValveRotated:
                    view.PlayRotate();
                    _sfx.Play(Sfx.Rotate);
                    if (res.AutoExit) Later(res.Delay, () => CompleteValve(pipe.Id, _session));
                    break;
            }
            HandleEvents(res.Events);
            RefreshHud();
        }

        private void CompleteValve(string id, GameSession session)
        {
            if (session != _session || _session.Status != SessionStatus.Playing) return;
            HandleEvents(_session.CompleteValveExit(id));
            RefreshHud();
        }

        private void HandleEvents(List<GameEvent> events)
        {
            if (events == null) return;
            bool ice = false;
            foreach (var ev in events)
            {
                var view = _board.ViewFor(ev.PipeId);
                switch (ev.Kind)
                {
                    case GameEventKind.Removed:
                        view?.PlayExit(ev.Cause);
                        if (ev.Cause == ExitCause.Tap || ev.Cause == ExitCause.Valve) _sfx.Water(_session.Combo);
                        break;
                    case GameEventKind.IceCracked:
                        view?.PlayBadgePop();
                        ice = true;
                        break;
                    case GameEventKind.Thawed:
                        if (view != null)
                        {
                            _board.Burst(view.Pipe, Color.white, 18, 1.1f);
                            view.Rebuild();
                        }
                        _sfx.Play(Sfx.Ice);
                        break;
                    case GameEventKind.Unlocked:
                        view?.PlayUnlock();
                        _sfx.Play(Sfx.Unlock);
                        break;
                    case GameEventKind.LeakBurst:
                        if (view != null)
                        {
                            view.PlayShake();
                            view.PlayBadgePop();
                            _board.Burst(view.Pipe, PaletteColors.Hex(Palette.Water), 22, 1.4f);
                        }
                        _sfx.Play(Sfx.Leak);
                        ToastOnce("leak", "Sızıntı! Yeşil boru sayacı bitmeden onu çıkar.");
                        break;
                    case GameEventKind.Won:
                        OnWin();
                        break;
                    case GameEventKind.Lost:
                        OnLose();
                        break;
                }
            }
            if (ice) _sfx.Play(Sfx.Ice, 0.6f);
            foreach (var v in _board.Pipes) v.RefreshBadge();
        }

        // ------------------------------------------------------------------ boosters
        private static string BoosterName(BoosterType b) => b == BoosterType.XRay ? "Röntgen" : b == BoosterType.Hammer ? "Çekiç" : "Yama";

        private void OnBooster(BoosterType b)
        {
            if (_fsm.Current != GameState.Playing && _fsm.Current != GameState.Hammer) return;
            _sfx.Play(Sfx.Click);
            if (b == BoosterType.Hammer && _fsm.Current == GameState.Hammer)
            {
                CancelHammer();
                return;
            }
            switch (_boosters.Check(b, _session))
            {
                case BoosterStatus.Locked:
                    _hud.Toast(BoosterName(b) + " " + BoosterRules.UnlockLevel(b) + ". seviyede açılır.");
                    return;
                case BoosterStatus.Empty:
                    ShowBoosterShop(b);
                    return;
                case BoosterStatus.NotApplicable:
                    if (b == BoosterType.Patch) _hud.Toast("Damlalar zaten dolu (en fazla 4).");
                    return;
            }
            if (b == BoosterType.XRay)
            {
                _boosters.UseXRay(_session, out var ids);
                if (ids.Count == 0) _hud.Toast("Şu an çıkabilen boru yok — önce pası kır ya da vanayı çevir.");
                _sfx.Play(Sfx.Coin, 0.5f);
            }
            else if (b == BoosterType.Hammer)
            {
                _boosters.ArmHammer(_session);
                _fsm.Go(GameState.Hammer);
                _hud.Toast("Kırmak istediğin boruya dokun.");
            }
            else
            {
                _boosters.UsePatch(_session);
                _sfx.Play(Sfx.Coin);
            }
            RefreshHud();
        }

        private void CancelHammer()
        {
            _boosters.CancelHammer();
            if (_fsm.Current == GameState.Hammer) _fsm.Go(GameState.Playing);
        }

        private void UseHammer(Pipe pipe)
        {
            var events = _boosters.ApplyHammer(_session, pipe.Id);
            if (_fsm.Current == GameState.Hammer) _fsm.Go(GameState.Playing);
            if (events == null) return;
            _sfx.Play(Sfx.Hammer);
            _board.Burst(pipe, PaletteColors.Hex(Palette.ColorFor(pipe, _session.HasLeaking)), 26, 1.6f);
            HandleEvents(events);
            RefreshHud();
        }

        // ------------------------------------------------------------------ win / lose
        private void OnWin()
        {
            _fsm.Go(GameState.Won);
            _hud.HideTooltip();
            int level = _session.LevelNumber;
            Later(0.45f, () =>
            {
                _board.Tank?.Drain();
                _sfx.Play(Sfx.Drain);
            });
            Later(1.9f, () =>
            {
                var reward = _economy.GrantWinReward(level, _session.Mistakes);
                _economy.UnlockUpTo(Mathf.Min(_index + 1, _repo.Count - 1));
                _analytics.TrackLevelComplete(level, _session.Mistakes, _session.Taps, _session.Leaks, _clock.Now - _startedAt, _boosters.UsedThisLevel, reward.Total);
                if (level == GameRules.FtueLevels) FtueStep("ftue_complete");
                _sfx.Play(Sfx.Win);
                ShowWin(reward);
            });
        }

        private void ShowWin(WinReward reward)
        {
            int level = _session.LevelNumber;
            bool flawless = _session.Mistakes == 0;
            var lines = "Seviye ödülü  +" + reward.Base;
            if (reward.Flawless > 0) lines += "\nKusursuz bonus  +" + reward.Flawless;
            if (reward.Breather > 0) lines += "\nMola seviyesi bonusu  +" + reward.Breather;
            lines += "\n<b>Toplam  +" + reward.Total + " jeton</b>\nRütbe: " + Progression.RankFor(level);
            if (Progression.RankFor(level + 1) != Progression.RankFor(level)) lines += "  →  Yeni rütbe: " + Progression.RankFor(level + 1) + "!";
            bool last = _index >= _repo.Count - 1;
            _hud.ShowModal(SpriteLibrary.Load("Sprites/Mascot/mascot_happy"), flawless ? "Kusursuz İş, Usta!" : "Harika İş!", lines, new[]
            {
                new ModalButton(last ? "Bölüm Seç" : "Devam Et ▶", HudView.GreenC, () =>
                {
                    if (last) ShowLevelSelect();
                    else _gate.AfterWin(level, () => StartLevel(_index + 1));
                }),
                new ModalButton("Bölümler", HudView.White, ShowLevelSelect),
            }, happy: true);
            _sfx.Play(Sfx.Coin);
        }

        private void OnLose()
        {
            _fsm.Go(GameState.Lost);
            _hud.HideTooltip();
            _economy.RecordFail();
            _analytics.TrackLevelFail(_session.LevelNumber, _session.Taps, Math.Round(_session.WaterRatio, 2), _clock.Now - _startedAt);
            _gate.AfterFail(); // GDD: never an interstitial on fail
            Later(0.65f, () =>
            {
                _sfx.Play(Sfx.Lose);
                ShowLose();
            });
        }

        private void ShowLose()
        {
            var buttons = new List<ModalButton>();
            int patches = _economy.BoosterCount(BoosterType.Patch);
            if (patches > 0 && Progression.BoosterUnlocked(BoosterType.Patch, _session.LevelNumber))
                buttons.Add(new ModalButton("Yama ile Devam", HudView.Blue, () =>
                {
                    _economy.TryConsumeBooster(BoosterType.Patch);
                    _analytics.TrackBoosterUsed("patch", _session.LevelNumber, _economy.BoosterCount(BoosterType.Patch));
                    Revive();
                }, "1 Yama kullan (" + patches + " adet)"));
            if (!_revived)
                buttons.Add(new ModalButton("+1 Damla ile Devam", HudView.Yellow, () => _gate.ShowRewarded("revive", _session.LevelNumber, ok =>
                {
                    if (!ok) return;
                    _revived = true;
                    Revive();
                }), "Ödüllü reklam izle"));
            buttons.Add(new ModalButton("Tekrar Dene ↻", HudView.GreenC, () => StartLevel(_index)));
            buttons.Add(new ModalButton("Bölümler", HudView.White, ShowLevelSelect));
            _hud.ShowModal(SpriteLibrary.Load("Sprites/Mascot/mascot_worried"), "Eyvah, Su Kaçtı!",
                "Damlalar bitti. Depoda hâlâ %" + Mathf.RoundToInt(_session.WaterRatio * 100) + " su var.", buttons, worried: true);
        }

        private void Revive()
        {
            if (!_session.Revive(1)) return;
            _hud.CloseModal();
            _fsm.Go(GameState.Playing);
            _sfx.Play(Sfx.Coin);
            RefreshHud();
        }

        // ------------------------------------------------------------------ menus
        private void Pause()
        {
            if (_fsm.Current == GameState.Paused) return;
            if (_fsm.Current == GameState.Hammer) CancelHammer();
            _resume = _fsm.Current;
            if (_fsm.CanGo(GameState.Paused)) _fsm.Go(GameState.Paused);
        }

        private void Resume()
        {
            _hud.CloseModal();
            if (_fsm.Current == GameState.Paused) _fsm.Go(_resume == GameState.Boot ? GameState.Playing : _resume);
            if (_fsm.Current == GameState.Won || _fsm.Current == GameState.Lost)
            {
                // closing a menu after the result: continue sensibly
                if (_fsm.Current == GameState.Won) StartLevel(_index + 1);
                else StartLevel(_index);
            }
        }

        private void ShowIntro(MechanicIntro intro)
        {
            Pause();
            _hud.ShowModal(SpriteLibrary.Load(intro.Sprite), "Yeni Boru: " + intro.Title, intro.Text, new[]
            {
                new ModalButton("Anladım!", HudView.GreenC, () =>
                {
                    _economy.Progress.SeenIntros.Add(intro.Type.ToJson());
                    _economy.Persist();
                    Resume();
                    AfterIntro();
                }),
            });
        }

        private void ShowSettings()
        {
            Pause();
            _hud.ShowModal(null, "Ayarlar", null, new[]
            {
                new ModalButton("Ses: " + (_economy.Progress.SoundOn ? "Açık" : "Kapalı"), HudView.White, () => { ToggleSound(); ShowSettings(); }),
                new ModalButton("Seviyeyi Yeniden Başlat ↻", HudView.Yellow, () => StartLevel(_index)),
                new ModalButton("Bölüm Seç", HudView.GreenC, ShowLevelSelect),
                new ModalButton("Mağaza", HudView.Blue, ShowShop),
                new ModalButton("İlerlemeyi Sıfırla", HudView.Red, () => { _economy.ResetProgress(); StartLevel(0); }),
                new ModalButton("Kapat", HudView.White, Resume),
            });
        }

        private void ShowShop()
        {
            Pause();
            var buttons = new List<ModalButton>();
            foreach (var p in _iap.Products)
            {
                var product = p;
                bool owned = product.RemovesAds && _economy.Progress.NoAds;
                buttons.Add(new ModalButton(product.Title, HudView.Yellow, () => _economy.Purchase(product.Id, ok =>
                {
                    if (ok) _sfx.Play(Sfx.Coin);
                    ShowShop();
                }), owned ? "Alındı" : product.Price.ToString("0.00") + " ₺ (mock)", !owned));
            }
            buttons.Add(new ModalButton("Kapat", HudView.White, Resume));
            _hud.ShowModal(null, "Mağaza", "Jeton: " + _economy.Coins + "\nSatın alımlar simülasyondur (mock IAP).", buttons);
        }

        private void ShowBoosterShop(BoosterType b)
        {
            Pause();
            int price = BoosterRules.CoinPrice(b);
            string sprite = "Sprites/UI/booster_" + BoosterRules.Id(b);
            _hud.ShowModal(SpriteLibrary.Load(sprite), BoosterName(b), "Envanter boş. Jeton: " + _economy.Coins, new[]
            {
                new ModalButton("Satın Al", HudView.Yellow, () => { if (_economy.TryBuyBooster(b)) _sfx.Play(Sfx.Coin); Resume(); }, price + " jeton", _economy.Coins >= price),
                new ModalButton("Reklam İzle", HudView.Blue, () => _gate.ShowRewarded("booster_" + BoosterRules.Id(b), _session.LevelNumber, ok =>
                {
                    if (ok) _economy.AddBooster(b);
                    Resume();
                }), "+1 " + BoosterName(b)),
                new ModalButton("Vazgeç", HudView.White, Resume),
            });
        }

        private void ShowLevelSelect()
        {
            Pause();
            var completed = new HashSet<int>(_economy.Progress.CompletedLevels);
            _hud.ShowLevelGrid("Bölümler", _repo.Entries, i => i <= _economy.Progress.MaxLevelIndex, l => completed.Contains(l), _index, StartLevel, Resume);
        }

        private void ToggleSound()
        {
            _economy.Progress.SoundOn = !_economy.Progress.SoundOn;
            _sfx.Enabled = _economy.Progress.SoundOn;
            _economy.Persist();
            _hud.Toast(_economy.Progress.SoundOn ? "Ses açık" : "Ses kapalı");
        }

        // ------------------------------------------------------------------ FTUE
        private bool FtueStep(string step)
        {
            if (_economy.Progress.FtueSteps.Contains(step)) return false;
            _economy.Progress.FtueSteps.Add(step);
            _economy.Persist();
            _analytics.TrackFtueStep(step, _session?.LevelNumber ?? 0);
            return true;
        }

        private void StartFtue(int level)
        {
            if (level > GameRules.FtueLevels) return;
            if (level == 1)
            {
                _hand = _session.ExitablePipeIds().FirstOrDefault();
                _board.ShowHand(_hand);
                FtueStep("l1_tap_hint");
                var target = _session.Board.Get(_hand);
                bool upper = target != null && target.Cells.Average(c => c.y) < _session.Board.Height / 2f;
                _hud.Tooltip("Okun gösterdiği yol açıksa boruya dokun — boru kayar, su boşalır!", upper);
            }
            else if (level == 2)
            {
                FtueStep("l2_blocking_hint");
                _hud.Tooltip("Önü kapalı boru takılır. Önce yolu açan boruyu çıkar!", false);
                Later(5f, _hud.HideTooltip);
            }
            else
            {
                FtueStep("l3_goal_hint");
                _hud.Tooltip("Tüm sulu boruları çıkar, depoyu boşalt! İlk 3 seviyede damla kaybetmezsin.", false);
                Later(5f, _hud.HideTooltip);
            }
        }

        // ------------------------------------------------------------------ helpers
        private void ToastOnce(string key, string msg)
        {
            if (_toasted.Add(key)) _hud.Toast(msg, 3.2f);
        }

        private void Later(float seconds, Action action) => _levelRoutines.Add(StartCoroutine(LaterRoutine(seconds, action)));

        private static IEnumerator LaterRoutine(float seconds, Action action)
        {
            yield return new WaitForSeconds(seconds);
            action();
        }

        private void RefreshHud()
        {
            if (_session == null || _hud == null) return;
            int level = _session.LevelNumber;
            _hud.SetLevel(level, Progression.ThemeFor(level), Progression.RankFor(level), Progression.IsBreather(level));
            _hud.SetCoins(_economy.Coins);
            _hud.SetLives(_session.Leaks, Mathf.Max(_session.Level.Leaks, _session.Leaks), _session.Ftue);
            foreach (var b in BoosterRules.All)
                _hud.SetBooster(b, _economy.BoosterCount(b), !Progression.BoosterUnlocked(b, level), BoosterRules.UnlockLevel(b),
                    b == BoosterType.Hammer && _fsm.Current == GameState.Hammer);
        }
    }

    /// <summary>Creates the game automatically in any scene (no scene/prefab setup needed). Add this component to opt out.</summary>
    public sealed class DisableSuUstasiAutoBoot : MonoBehaviour { }

    public static class GameBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
#if UNITY_2023_1_OR_NEWER
            if (UnityEngine.Object.FindFirstObjectByType<GameStateMachine>() != null) return;
            if (UnityEngine.Object.FindFirstObjectByType<DisableSuUstasiAutoBoot>() != null) return;
#else
            if (UnityEngine.Object.FindObjectOfType<GameStateMachine>() != null) return;
            if (UnityEngine.Object.FindObjectOfType<DisableSuUstasiAutoBoot>() != null) return;
#endif
            new GameObject("SuUstasi").AddComponent<GameStateMachine>();
        }
    }
}
