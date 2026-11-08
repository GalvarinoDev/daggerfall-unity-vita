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

namespace DaggerfallWorkshop.Utility
{
    /// <summary>
    /// General purpose material animation.
    /// Works by swapping texture maps on target material from an array of other materials.
    /// </summary>
    public class AnimatedMaterial : MonoBehaviour
    {
        public Material TargetMaterial;
        public CachedMaterial[] AnimationFrames;
        public float FramesPerSecond = 12;

        bool restartAnims;
#if UNITY_PSP2
        static readonly int BumpMapId = Shader.PropertyToID("_BumpMap");
        static readonly int EmissionMapId = Shader.PropertyToID("_EmissionMap");
#endif

        void Start()
        {
            StartCoroutine(AnimateTextures());
        }

        void OnDisable()
        {
            restartAnims = true;
        }

        void OnEnable()
        {
            // Restart animation coroutine if not running
            if (restartAnims)
            {
                StartCoroutine(AnimateTextures());
                restartAnims = false;
            }
        }

        IEnumerator AnimateTextures()
        {
            int currentTexture = 0;
#if UNITY_PSP2
            // Every tick of every animated model: no new WaitForSeconds, no string property lookups (garbage at 12 Hz x 26 models)
            WaitForSeconds wait = new WaitForSeconds(1f / FramesPerSecond);
            while (true)
            {
                if (AnimationFrames != null && TargetMaterial != null)
                {
                    TargetMaterial.mainTexture = AnimationFrames[currentTexture].albedoMap;
                    TargetMaterial.SetTexture(BumpMapId, AnimationFrames[currentTexture].normalMap);
                    TargetMaterial.SetTexture(EmissionMapId, AnimationFrames[currentTexture].emissionMap);
                    if (++currentTexture >= AnimationFrames.Length)
                        currentTexture = 0;
                }

                yield return wait;
            }
#else
            while (true)
            {
                if (AnimationFrames != null && TargetMaterial != null)
                {
                    TargetMaterial.mainTexture = AnimationFrames[currentTexture].albedoMap;
                    TargetMaterial.SetTexture("_BumpMap", AnimationFrames[currentTexture].normalMap);
                    TargetMaterial.SetTexture("_EmissionMap", AnimationFrames[currentTexture].emissionMap);
                    if (++currentTexture >= AnimationFrames.Length)
                        currentTexture = 0;
                }

                yield return new WaitForSeconds(1f / FramesPerSecond);
            }
#endif
        }
    }
}