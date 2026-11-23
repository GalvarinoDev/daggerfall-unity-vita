using System;
using UnityEngine;

namespace UnityStandardAssets.Water
{
    [ExecuteInEditMode]
    public class WaterBasic : MonoBehaviour
    {
#if UNITY_PSP2
        static readonly int vitaWaveSpeedId = Shader.PropertyToID("WaveSpeed");
        static readonly int vitaWaveScaleId = Shader.PropertyToID("_WaveScale");
        static readonly int vitaWaveOffsetId = Shader.PropertyToID("_WaveOffset");
        static int vitaLastMaterialId, vitaLastFrame = -1;
        Renderer vitaRenderer;

        // Vita: the renderer and property ids cached, and a material shared by several water planes set once per frame (it gets the same
        // value from each): 6 planes in the Daggerfall Palace cost 0.6 ms a frame
        void Update()
        {
            if (!vitaRenderer)
                vitaRenderer = GetComponent<Renderer>();
            Renderer r = vitaRenderer;
            if (!r)
            {
                return;
            }
            Material mat = r.sharedMaterial;
            if (!mat)
            {
                return;
            }
            int id = mat.GetInstanceID();
            if (id == vitaLastMaterialId && Time.frameCount == vitaLastFrame)
            {
                return;
            }
            vitaLastMaterialId = id;
            vitaLastFrame = Time.frameCount;

            Vector4 waveSpeed = mat.GetVector(vitaWaveSpeedId);
            float waveScale = mat.GetFloat(vitaWaveScaleId);
            float t = Time.time / 20.0f;

            Vector4 offset4 = waveSpeed * (t * waveScale);
            Vector4 offsetClamped = new Vector4(Mathf.Repeat(offset4.x, 1.0f), Mathf.Repeat(offset4.y, 1.0f),
                Mathf.Repeat(offset4.z, 1.0f), Mathf.Repeat(offset4.w, 1.0f));
            mat.SetVector(vitaWaveOffsetId, offsetClamped);
        }
#else
        void Update()
        {
            Renderer r = GetComponent<Renderer>();
            if (!r)
            {
                return;
            }
            Material mat = r.sharedMaterial;
            if (!mat)
            {
                return;
            }

            Vector4 waveSpeed = mat.GetVector("WaveSpeed");
            float waveScale = mat.GetFloat("_WaveScale");
            float t = Time.time / 20.0f;

            Vector4 offset4 = waveSpeed * (t * waveScale);
            Vector4 offsetClamped = new Vector4(Mathf.Repeat(offset4.x, 1.0f), Mathf.Repeat(offset4.y, 1.0f),
                Mathf.Repeat(offset4.z, 1.0f), Mathf.Repeat(offset4.w, 1.0f));
            mat.SetVector("_WaveOffset", offsetClamped);
        }
#endif
    }
}