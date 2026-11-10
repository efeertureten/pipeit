using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM && SU_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

namespace SuUstasi.Runtime
{
    /// <summary>Single-touch / mouse input that works with the new Input System package or the legacy Input Manager.</summary>
    public static class PointerInput
    {
        private static readonly List<RaycastResult> Hits = new List<RaycastResult>();

        public static bool TryGetPress(out Vector2 screen)
        {
#if ENABLE_INPUT_SYSTEM && SU_INPUT_SYSTEM
            var p = Pointer.current;
            if (p != null && p.press.wasPressedThisFrame)
            {
                screen = p.position.ReadValue();
                return true;
            }
#else
            if (Input.touchCount > 0)
            {
                var t = Input.GetTouch(0);
                if (t.phase == TouchPhase.Began)
                {
                    screen = t.position;
                    return true;
                }
            }
            else if (Input.GetMouseButtonDown(0))
            {
                screen = Input.mousePosition;
                return true;
            }
#endif
            screen = default;
            return false;
        }

        public static bool KeyDown(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM && SU_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k == null) return false;
            switch (key)
            {
                case KeyCode.R: return k.rKey.wasPressedThisFrame;
                case KeyCode.M: return k.mKey.wasPressedThisFrame;
                case KeyCode.Escape: return k.escapeKey.wasPressedThisFrame;
                default: return false;
            }
#else
            return Input.GetKeyDown(key);
#endif
        }

        /// <summary>True when the screen point hits any uGUI element (so board taps don't go through buttons).</summary>
        public static bool IsOverUi(Vector2 screen)
        {
            var es = EventSystem.current;
            if (es == null) return false;
            Hits.Clear();
            es.RaycastAll(new PointerEventData(es) { position = screen }, Hits);
            return Hits.Count > 0;
        }

        public static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM && SU_INPUT_SYSTEM
            go.AddComponent<InputSystemUIInputModule>();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }
    }
}
