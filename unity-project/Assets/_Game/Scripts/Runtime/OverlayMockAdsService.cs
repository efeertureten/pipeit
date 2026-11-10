using System;
using System.Collections;
using SuUstasi.Services;
using UnityEngine;

namespace SuUstasi.Runtime
{
    /// <summary>Development ads: a visible "mock" card with a countdown, so ad pacing can be play-tested without an SDK.</summary>
    public sealed class OverlayMockAdsService : IAdsService
    {
        private readonly HudView _hud;
        private readonly MonoBehaviour _host;

        public OverlayMockAdsService(HudView hud, MonoBehaviour host)
        {
            _hud = hud;
            _host = host;
        }

        public bool IsInterstitialReady => true;
        public bool IsRewardedReady => true;

        public void ShowInterstitial(string placement, Action onClosed) => _host.StartCoroutine(Run("Reklam", "Sponsorlu içerik (mock interstitial)", 3f, "Kapat", () => onClosed?.Invoke()));

        public void ShowRewarded(string placement, Action<bool> onCompleted) => _host.StartCoroutine(Run("Ödüllü Reklam", "Video oynuyor… (mock rewarded)", 1.5f, "Ödülü Al", () => onCompleted?.Invoke(true)));

        private IEnumerator Run(string title, string body, float seconds, string close, Action done)
        {
            for (float left = seconds; left > 0; left -= 1f)
            {
                _hud.ShowModal(null, title, body + "\n" + Mathf.CeilToInt(left), new[] { new ModalButton(close + " (" + Mathf.CeilToInt(left) + ")", HudView.White, null, null, false) });
                yield return new WaitForSecondsRealtime(Mathf.Min(1f, left));
            }
            _hud.ShowModal(null, title, body, new[]
            {
                new ModalButton(close, HudView.GreenC, () =>
                {
                    _hud.CloseModal();
                    done();
                }),
            });
        }
    }
}
