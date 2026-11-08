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
using DaggerfallWorkshop.Game.MagicAndEffects;

namespace DaggerfallWorkshop.Game.Entity
{
    /// <summary>
    /// Handles magical concealment for entities other than player.
    /// Inherit from this class for custom entity concealment handling.
    /// </summary>
    [RequireComponent(typeof(DaggerfallEntityBehaviour))]
    [RequireComponent(typeof(EntityEffectManager))]
    public class EntityConcealmentBehaviour : MonoBehaviour
    {
        protected DaggerfallEntityBehaviour entityBehaviour;
        protected EntityEffectManager entityEffectManager;
        protected MeshRenderer meshRenderer;
#if UNITY_PSP2
        bool vitaConcealedSet, vitaConcealed;
#endif

        #region Unity

        void Awake()
        {
            CacheReferences();
        }

        void Update()
        {
            if (!entityBehaviour || entityBehaviour.Entity == null)
                return;

            bool isConcealed = (entityBehaviour && entityBehaviour.Entity.IsMagicallyConcealed);
#if UNITY_PSP2
            // Nothing else switches this renderer: write it when the state changes, not per enemy per frame
            if (vitaConcealedSet && isConcealed == vitaConcealed)
                return;
            vitaConcealedSet = true;
            vitaConcealed = isConcealed;
#endif
            MakeConcealed(isConcealed);
        }

        #endregion

        #region Protected Methods

        protected void CacheReferences()
        {
            entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
            entityEffectManager = GetComponent<EntityEffectManager>();
            meshRenderer = GetComponentInChildren<MeshRenderer>();
        }

        protected virtual void MakeConcealed(bool concealed)
        {
            if (meshRenderer)
            {
                meshRenderer.enabled = !concealed;
            }
        }

        #endregion
    }
}
