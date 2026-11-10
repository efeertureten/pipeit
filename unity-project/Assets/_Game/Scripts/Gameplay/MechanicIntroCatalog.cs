using System.Collections.Generic;
using SuUstasi.Core;

namespace SuUstasi.Gameplay
{
    public sealed class MechanicIntro
    {
        public PipeType Type;
        public int Level;
        /// <summary>Resources path of the sliced sprite (tools/slice_sprites.py).</summary>
        public string Sprite;
        public string Title;
        public string Text;
    }

    /// <summary>"New pipe" cards shown the first time a mechanic appears (same copy as the web playable).</summary>
    public static class MechanicIntroCatalog
    {
        public static readonly IReadOnlyList<MechanicIntro> All = new List<MechanicIntro>
        {
            new MechanicIntro { Type = PipeType.Elbow, Level = 8, Sprite = "Sprites/Pipes/pipe_elbow", Title = "Dirsek Boru",
                Text = "L şeklindeki borular da okun yönünde bütün gövdesiyle kayar. Gövdenin her hücresinin önü açık olmalı." },
            new MechanicIntro { Type = PipeType.Dry, Level = 12, Sprite = "Sprites/Pipes/pipe_dry", Title = "Kuru Boru",
                Text = "İçinde su yok — kazanmak için çıkarman gerekmez. Ama yolu tıkıyorsa önce onu kaldır." },
            new MechanicIntro { Type = PipeType.Rusty, Level = 16, Sprite = "Sprites/Pipes/pipe_rusty", Title = "Paslı Boru",
                Text = "İlk dokunuş pası kırar ve damla kaybettirmez. İkinci dokunuşta yolu açıksa kayar." },
            new MechanicIntro { Type = PipeType.Frozen, Level = 22, Sprite = "Sprites/Pipes/pipe_frozen", Title = "Donmuş Boru",
                Text = "Üstündeki sayı kadar başka boru çıkınca buz çözülür. Erken dokunursan 1 damla kaybedersin." },
            new MechanicIntro { Type = PipeType.Locked, Level = 30, Sprite = "Sprites/Pipes/pipe_locked", Title = "Kilitli Boru & Anahtar Vana",
                Text = "Zincirli boru, aynı renkli anahtar vanalı boru çıkana kadar açılmaz. Erken dokunmak 1 damla." },
            new MechanicIntro { Type = PipeType.Leaking, Level = 35, Sprite = "Sprites/Pipes/pipe_leaking", Title = "Sızdıran Boru",
                Text = "Her dokunuşta sayacı 1 azalır. Sayaç bitmeden çıkarmazsan 1 damla kaybedersin (sayaç yeniden başlar)." },
            new MechanicIntro { Type = PipeType.RotaryValve, Level = 61, Sprite = "Sprites/Pipes/pipe_rotary_valve", Title = "Döner Vana",
                Text = "Dokununca saat yönünde 90° döner, damla kaybettirmez. Yol açılınca 0,5 sn sonra kendiliğinden kayar." },
        };

        /// <summary>The intro to show for this level (only if the level actually contains the mechanic).</summary>
        public static MechanicIntro For(LevelData level)
        {
            foreach (var intro in All)
            {
                if (intro.Level != level.Level) continue;
                foreach (var p in level.Pipes)
                    if (p.Type == intro.Type) return intro;
            }
            return null;
        }
    }
}
