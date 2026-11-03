// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Physics timing for the Vita. With the project's 50 Hz fixed step and 0.33 s catch-up cap, a 10 fps dungeon frame ran
// FixedUpdate (every enemy's motor/senses raycasts) 5-16 times, which made the frame slower still. 30 Hz matches the
// frame rate target (one step per frame); the 0.1 s cap allows at most 3 catch-up steps, and below 10 fps the game slows down
// instead of spiralling.

#if UNITY_PSP2
using UnityEngine;

public static class VitaTime
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Init()
    {
        // enemy AI and physics (incl. the player's motor; the camera follower still lerps every frame) step coarser. Back to 1f / 30f if it feels worse.
        Time.fixedDeltaTime = 1f / 20f;
        Time.maximumDeltaTime = 0.1f;
    }
}
#endif
