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
using DaggerfallConnect;
using DaggerfallWorkshop.Game.Weather;

namespace DaggerfallWorkshop.Game
{
    /// <summary>
    /// Script to play weather effects over player.
    /// Allows weather to be attached elsewhere than a child of player (such as exterior parent).
    /// In multiplayer do not sync weather particle effects to other players.
    /// </summary>
    [RequireComponent(typeof(PlayerGPS))]
    [RequireComponent(typeof(PlayerEnterExit))]
    public class PlayerWeather : MonoBehaviour
    {
        public GameObject RainParticles;
        public GameObject SnowParticles;
        public WeatherType WeatherType = WeatherType.Sunny;
        public PlayerGPS PlayerGps { get; private set; }
        public byte[] ClimateWeathers;

        PlayerEnterExit playerEnterExit;
        WeatherType _currentWeatherType = WeatherType.Sunny;
        DFLocation.ClimateBaseType currentClimateType = DFLocation.ClimateBaseType.None;
        bool isInside = false;

        void Start()
        {
            PlayerGps = GetComponent<PlayerGPS>();
            playerEnterExit = GetComponent<PlayerEnterExit>();
            if (RainParticles) RainParticles.SetActive(false);
            if (SnowParticles) SnowParticles.SetActive(false);
            ClimateWeathers = new byte[6];
#if UNITY_PSP2
            Lighten(RainParticles);
            Lighten(SnowParticles);
            if (VitaGroundPlane)
            {
                Transform ground = new GameObject("VitaWeatherGround").transform;
                ground.SetParent(transform, false);
                CharacterController controller = GetComponent<CharacterController>();
                ground.localPosition = controller ? controller.center + Vector3.down * (controller.height * 0.5f) : Vector3.down * 0.9f;
                foreach (GameObject weather in new[] { RainParticles, SnowParticles })
                    if (weather)
                        foreach (ParticleSystem particles in weather.GetComponentsInChildren<ParticleSystem>(true))
                        {
                            var collision = particles.collision;
                            if (!collision.enabled)
                                continue;
                            collision.type = ParticleSystemCollisionType.Planes;
                            collision.SetPlane(0, ground);
                        }
            }
#endif
        }
#if UNITY_PSP2

        // Kept on purpose: rain emits 2000 drops/s and snow 1000 flakes/s (5 s
        // lifetime), each colliding with the world at High quality, a physics query per particle per frame. That ran the exterior at
        // 6.9 fps in rain and 5.3 in snow vs 34 dry, at every resolution; with this, 26-27 fps. Low quality collides
        // against a cached voxel approximation instead (splashes land less exactly).
        const float density = 0.3f;
        public static bool VitaGroundPlane = true;
        const int maxParticles = 1500;

        static void Lighten(GameObject weather)
        {
            if (!weather)
                return;
            foreach (ParticleSystem particles in weather.GetComponentsInChildren<ParticleSystem>(true))
            {
                var emission = particles.emission;
                emission.rateOverTimeMultiplier *= density;
                var main = particles.main;
                main.maxParticles = Mathf.Min(main.maxParticles, maxParticles);
                var collision = particles.collision;
                if (collision.enabled)
                    collision.quality = ParticleSystemCollisionQuality.Low;
            }
        }
#endif

        void Update()
        {
            // Update weather if context changes
            if (WeatherType != _currentWeatherType ||
                playerEnterExit.IsPlayerInside != isInside ||
                PlayerGps.ClimateSettings.ClimateType != currentClimateType)
            {
                isInside = playerEnterExit.IsPlayerInside;
                currentClimateType = PlayerGps.ClimateSettings.ClimateType;
                _currentWeatherType = WeatherType;
                SetWeather();
            }
        }

        void SetWeather()
        {
            // Always disable weather inside
            if (isInside)
            {
                if (RainParticles) RainParticles.SetActive(false);
                if (SnowParticles) SnowParticles.SetActive(false);
                return;
            }

            switch (WeatherType)
            {
                case WeatherType.Rain:
                case WeatherType.Thunder:
                    if (RainParticles) RainParticles.SetActive(true);
                    if (SnowParticles) SnowParticles.SetActive(false);
                    break;
                case WeatherType.Snow:
                    if (RainParticles) RainParticles.SetActive(false);
                    if (SnowParticles) SnowParticles.SetActive(true);
                    break;
                default:
                    if (RainParticles) RainParticles.SetActive(false);
                    if (SnowParticles) SnowParticles.SetActive(false);
                    break;
            }
        }
    }
}