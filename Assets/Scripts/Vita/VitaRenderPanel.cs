// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// A panel that shows a RenderTexture under its children, in place of a Texture2D background copied from it with ReadPixels + Apply.
// The Vita player has no Graphics.CopyTexture, so each such copy waited for the GPU and uploaded the image again (UNITY_VITA_GUIDE §8):
// paper doll and both automaps (every open, pan, zoom and rotate).

#if UNITY_PSP2
using UnityEngine;

namespace DaggerfallWorkshop.Game.UserInterface
{
    public class VitaRenderPanel : Panel
    {
        public Texture Texture;   // drawn StretchToFill, as BackgroundLayout.StretchToFill drew the copy

        public override void Draw()
        {
            if (Enabled && Texture)
                DaggerfallUI.DrawTexture(Rectangle, Texture, ScaleMode.StretchToFill);
            base.Draw();
        }
    }
}
#endif
