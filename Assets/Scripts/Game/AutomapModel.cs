// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: XJDHDR
// Contributors:     
// 
// Notes:
//

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Utility;
using UnityEngine;

namespace DaggerfallWorkshop
{
    /// <summary>
    /// Attached to all Automap models to perform various functions specific to them.
    /// </summary>
    public sealed class AutomapModel : MonoBehaviour
    {
        [SerializeField]
        [HideInInspector]
        private bool subscribedToEvents = false;

        void Awake()
        {
            if (!subscribedToEvents)
            {
                Automap.OnInjectMeshAndMaterialProperties += Automap_OnInjectMeshAndMaterialProperties;
                subscribedToEvents = true;
            }
        }

        private void OnDestroy()
        {
            if (subscribedToEvents)
            {
                Automap.OnInjectMeshAndMaterialProperties -= Automap_OnInjectMeshAndMaterialProperties;
                subscribedToEvents = false;
            }
        }

        private void Automap_OnInjectMeshAndMaterialProperties(bool playerIsInsideBuilding, Vector3 playerAdvancedPos, Material automapMaterial, bool resetDiscoveryState = true)
        {
            Automap.OnInjectMeshAndMaterialProperties -= Automap_OnInjectMeshAndMaterialProperties;
            subscribedToEvents = false;

            // get rid of animated materials (will not break automap rendering but is not necessary)
            AnimatedMaterial[] animatedMaterials = gameObject.GetComponentsInChildren<AnimatedMaterial>();
            foreach (AnimatedMaterial animatedMaterial in animatedMaterials)
            {
                UnityEngine.Object.Destroy(animatedMaterial);
            }

            MeshRenderer[] meshRenderers = gameObject.GetComponentsInChildren<MeshRenderer>();
            if (meshRenderers == null)
                return;

            // Update materials. If inside an interior, set visitedInThisEntering parameter to True so that they are always coloured.
            // Otherwise, mark meshes as not visited in this run (so "Dungeon" geometry that has been discovered in a previous dungeon run is rendered in grayscale)
            foreach (MeshRenderer meshRenderer in meshRenderers)
            {
                UpdateMaterialsOfMeshRenderer(playerAdvancedPos, automapMaterial, meshRenderer, playerIsInsideBuilding);

                // if player is inside dungeon or castle and forced reset of discovery state.
                if ((!playerIsInsideBuilding) && (resetDiscoveryState))
                {
                    // mark meshRenderer as undiscovered
                    meshRenderer.enabled = false;
                }
            }
        }

#if UNITY_PSP2
        // Automap.VitaPrebuildDungeon injects each block's models in the block's own frame behind the loading window, instead of every
        // model in the entry frame (1.4-3.7 s); VitaPrebuildInterior does the same behind a building door's black screen. Same
        // arguments as the entry's event; no automap shader reads _PlayerPosition.
        public void VitaInject(Vector3 playerAdvancedPos, Material automapMaterial, bool playerIsInsideBuilding)
        {
            Automap_OnInjectMeshAndMaterialProperties(playerIsInsideBuilding, playerAdvancedPos, automapMaterial, true);
        }
#endif

        private void UpdateMaterialsOfMeshRenderer(Vector3 playerAdvancedPos, Material automapMaterial, MeshRenderer meshRenderer, bool visitedInThisEntering = false)
        {
#if UNITY_PSP2
            // One automap material per source material and visited state, shared by every renderer that uses it, instead of a clone per
            // renderer slot (1,275 in the start dungeon, 4,068 in Daggerfall's Palace, 5,919 in the biggest; 5,000 clones took ~6-8 MB of
            // engine heap and 3.4 s on hardware). Automap.cs swaps between the two variants where upstream toggles the keyword.
            if (!VitaSharedOff)
            {
                Material[] srcMaterials = meshRenderer.sharedMaterials;
                Material[] sharedMaterials = new Material[srcMaterials.Length];
                for (int i = 0; i < srcMaterials.Length; i++)
                    sharedMaterials[i] = VitaShared(srcMaterials[i], automapMaterial, playerAdvancedPos, !visitedInThisEntering);
                meshRenderer.sharedMaterials = sharedMaterials;
                return;
            }
#endif
#if UNITY_PSP2
            // Read the shared materials once: the .materials getter cloned every material of the renderer (all replaced right
            // below, so the clones were orphans until the next UnloadUnusedAssets) and made a new array per call
            Material[] curMaterials = meshRenderer.sharedMaterials;
            Material[] newMaterials = new Material[curMaterials.Length];
            for (int i = 0; i < curMaterials.Length; i++)
            {
                Material curMaterial = curMaterials[i];
#else
            Material[] newMaterials = new Material[meshRenderer.materials.Length];
            for (int i = 0; i < meshRenderer.materials.Length; i++)
            {
                Material curMaterial = meshRenderer.materials[i];
#endif
#if UNITY_PSP2
                bool vitaPalette = curMaterial.HasProperty(VitaPalette.PaletteTex);
                Material newMaterial = Instantiate(vitaPalette ? VitaPaletteTemplate(automapMaterial) : automapMaterial);
#else
                Material newMaterial = Instantiate(automapMaterial);
#endif

                //newMaterial.CopyPropertiesFromMaterial(material);
                newMaterial.name = "AutomapBelowSclicePlane injected for: " + curMaterial.name;
                if (curMaterial.HasProperty(Uniforms.MainTex))
                    newMaterial.SetTexture(Uniforms.MainTex, curMaterial.GetTexture(Uniforms.MainTex));
#if UNITY_PSP2
                // Palette-index textures (VitaPalette) need the automap shader's palette variant
                if (vitaPalette)
                    newMaterial.SetTexture(VitaPalette.PaletteTex, curMaterial.GetTexture(VitaPalette.PaletteTex));
#endif
                if (curMaterial.HasProperty(Uniforms.BumpMap))
                    newMaterial.SetTexture(Uniforms.BumpMap, curMaterial.GetTexture(Uniforms.BumpMap));
                if (curMaterial.HasProperty(Uniforms.EmissionMap))
                    newMaterial.SetTexture(Uniforms.EmissionMap, curMaterial.GetTexture(Uniforms.EmissionMap));
                if (curMaterial.HasProperty(Uniforms.EmissionColor))
                    newMaterial.SetColor(Uniforms.EmissionColor, curMaterial.GetColor(Uniforms.EmissionColor));
                Vector4 playerPosition = new Vector4(playerAdvancedPos.x, playerAdvancedPos.y + Camera.main.transform.localPosition.y, playerAdvancedPos.z, 0.0f);
                newMaterial.SetVector("_PlayerPosition", playerPosition);
                if (visitedInThisEntering == true)
                    newMaterial.DisableKeyword("RENDER_IN_GRAYSCALE");
                else
                    newMaterial.EnableKeyword("RENDER_IN_GRAYSCALE");
                newMaterials[i] = newMaterial;
            }
            meshRenderer.materials = newMaterials;
        }

#if UNITY_PSP2
        // automapMaterial with the palette shader, made once per frame (all models inject in the same frame), instead of a
        // Shader.Find + shader swap on every injected material
        static Material vitaPaletteTemplate;
        static Material vitaPaletteTemplateSource;
        static int vitaPaletteTemplateFrame = -1;

        static Material VitaPaletteTemplate(Material automapMaterial)
        {
            if (vitaPaletteTemplate == null || vitaPaletteTemplateSource != automapMaterial || vitaPaletteTemplateFrame != Time.frameCount)
            {
                if (vitaPaletteTemplate != null)
                    Destroy(vitaPaletteTemplate);
                vitaPaletteTemplate = Instantiate(automapMaterial);
                vitaPaletteTemplate.shader = Shader.Find(VitaPalette.AutomapShaderName);
                vitaPaletteTemplateSource = automapMaterial;
                vitaPaletteTemplateFrame = Time.frameCount;
            }
            return vitaPaletteTemplate;
        }

        public static bool VitaSharedOff;   // test harness A/B: a clone per renderer slot, as upstream
        // source material -> {colour, grey}, and each variant -> the same pair (for the swaps in Automap.cs)
        static readonly System.Collections.Generic.Dictionary<Material, Material[]> vitaVariants = new System.Collections.Generic.Dictionary<Material, Material[]>();
        static readonly System.Collections.Generic.Dictionary<Material, Material[]> vitaPairOf = new System.Collections.Generic.Dictionary<Material, Material[]>();
        static Material vitaVariantsTemplate;

        // New automap geometry (dungeon or interior rebuilt): forget the old variants. Not destroyed here: renderers of the old geometry
        // may still draw this frame; the transition's UnloadUnusedAssets frees them with it, as it freed the per-renderer clones.
        public static void VitaResetShared()
        {
            vitaVariants.Clear();
            vitaPairOf.Clear();
        }

        static Material VitaShared(Material curMaterial, Material automapMaterial, Vector3 playerAdvancedPos, bool grayscale)
        {
            if (automapMaterial != vitaVariantsTemplate)
            {
                VitaResetShared();
                vitaVariantsTemplate = automapMaterial;
            }
            Material[] pair;
            if (curMaterial == null || !vitaVariants.TryGetValue(curMaterial, out pair))
            {
                pair = new Material[] { VitaMakeVariant(curMaterial, automapMaterial, playerAdvancedPos, false),
                    VitaMakeVariant(curMaterial, automapMaterial, playerAdvancedPos, true) };
                if (curMaterial != null)
                {
                    vitaVariants[curMaterial] = pair;
                    vitaPairOf[pair[0]] = pair;
                    vitaPairOf[pair[1]] = pair;
                }
            }
            return pair[grayscale ? 1 : 0];
        }

        // The same properties UpdateMaterialsOfMeshRenderer copies into each clone
        static Material VitaMakeVariant(Material curMaterial, Material automapMaterial, Vector3 playerAdvancedPos, bool grayscale)
        {
            bool vitaPalette = curMaterial != null && curMaterial.HasProperty(VitaPalette.PaletteTex);
            Material newMaterial = Instantiate(vitaPalette ? VitaPaletteTemplate(automapMaterial) : automapMaterial);
            newMaterial.name = "AutomapBelowSclicePlane injected for: " + (curMaterial != null ? curMaterial.name : "(none)") + (grayscale ? " (grey)" : "");
            if (curMaterial != null)
            {
                if (curMaterial.HasProperty(Uniforms.MainTex))
                    newMaterial.SetTexture(Uniforms.MainTex, curMaterial.GetTexture(Uniforms.MainTex));
                if (vitaPalette)
                    newMaterial.SetTexture(VitaPalette.PaletteTex, curMaterial.GetTexture(VitaPalette.PaletteTex));
                if (curMaterial.HasProperty(Uniforms.BumpMap))
                    newMaterial.SetTexture(Uniforms.BumpMap, curMaterial.GetTexture(Uniforms.BumpMap));
                if (curMaterial.HasProperty(Uniforms.EmissionMap))
                    newMaterial.SetTexture(Uniforms.EmissionMap, curMaterial.GetTexture(Uniforms.EmissionMap));
                if (curMaterial.HasProperty(Uniforms.EmissionColor))
                    newMaterial.SetColor(Uniforms.EmissionColor, curMaterial.GetColor(Uniforms.EmissionColor));
            }
            newMaterial.SetVector("_PlayerPosition", new Vector4(playerAdvancedPos.x, playerAdvancedPos.y + Camera.main.transform.localPosition.y, playerAdvancedPos.z, 0.0f));
            if (grayscale)
                newMaterial.EnableKeyword("RENDER_IN_GRAYSCALE");
            else
                newMaterial.DisableKeyword("RENDER_IN_GRAYSCALE");
            return newMaterial;
        }

        // Automap.cs: mark a renderer visited (colour) or not (grey) by swapping to the other shared variant. False when a slot isn't one of
        // ours (VitaSharedOff, or geometry built before a reset): the caller then toggles the keyword as upstream does.
        public static bool VitaSetGrayscale(MeshRenderer renderer, bool grayscale)
        {
            if (VitaSharedOff)
                return false;
            Material[] mats = renderer.sharedMaterials;
            Material[] pair;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null || !vitaPairOf.TryGetValue(mats[i], out pair))
                    return false;
                mats[i] = pair[grayscale ? 1 : 0];
            }
            renderer.sharedMaterials = mats;
            return true;
        }

        // Automap.cs: the visited state as upstream reads it from materials[0] (sharedMaterial: .materials would clone the shared variants)
        public static bool VitaIsGrayscale(MeshRenderer renderer)
        {
            Material m = renderer.sharedMaterial;
            return m != null && m.IsKeywordEnabled("RENDER_IN_GRAYSCALE");
        }
#endif
    }
}
