// Audio filter of DaggerfallSongPlayer's C# synth, a component of its own so that songs with a pre-rendered clip run no C# on the mixer thread.
#if UNITY_PSP2
using UnityEngine;

namespace DaggerfallWorkshop
{
    public class VitaSongFilter : MonoBehaviour
    {
        public DaggerfallSongPlayer Owner;

        void OnAudioFilterRead(float[] data, int channels)
        {
            Owner.VitaFilterRead(data, channels);
        }
    }
}
#endif
