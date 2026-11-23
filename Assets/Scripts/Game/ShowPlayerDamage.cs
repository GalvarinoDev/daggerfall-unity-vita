// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors:    
// 
// Notes:
//

using UnityEngine;
using System.Collections;

namespace DaggerfallWorkshop.Game
{
    /// <summary>
    /// Simple component to flash screen red on player damage.
    /// </summary>
    public class ShowPlayerDamage : MonoBehaviour
    {
        Texture2D damageTexture;
        bool fadingOut = false;
        float alphaFadeValue = 0;
        float fadeSpeed = 0.7f;

        void Start()
        {
            if (damageTexture == null)
#if UNITY_PSP2
            {
                // Vita: the flash is one solid colour stretched to the screen, so 1x1 draws the same. The full-screen
                // texture cost 2 MB for good plus an 8 MB Color[] at boot (the IL2CPP heap never shrinks).
                damageTexture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                damageTexture.SetPixel(0, 0, new Color(1, 0, 0, 1));
                damageTexture.Apply(false, true);
            }
#else
                damageTexture = __ExternalAssets.iTween.CameraTexture(new Color(1, 0, 0, 1));
#endif
        }

#if UNITY_PSP2
        void OnDestroy()
        {
            if (damageTexture != null)
                Destroy(damageTexture);
        }
#endif

        public void Flash()
        {
            alphaFadeValue = 0.4f;
            fadingOut = true;
        }

        void OnGUI()
        {
            if (fadingOut)
            {
                alphaFadeValue -= fadeSpeed * Time.deltaTime;
                if (alphaFadeValue > 0)
                {
                    Color color = new Color(1, 0, 0, alphaFadeValue);
                    DaggerfallUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), damageTexture, ScaleMode.StretchToFill, true, color);
                }
                else
                {
                    alphaFadeValue = 0;
                    fadingOut = false;
                }
            }
        }
    }
}