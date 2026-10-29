#if UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Hibira
{
    /// <summary>
    /// A hidden object that ticks every <see cref="HibiraClient"/> once a frame
    /// on the main thread, and closes their connections when the game quits.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class HibiraRunner : MonoBehaviour
    {
        static readonly List<HibiraClient> Clients = new List<HibiraClient>();
        static HibiraRunner _instance;

        // Domain reload can be turned off in the Editor; start clean every Play.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Clients.Clear();
            _instance = null;
        }

        internal static void Register(HibiraClient client)
        {
            if (!Clients.Contains(client)) Clients.Add(client);
            if (_instance != null || !Application.isPlaying) return;
            var go = new GameObject("Hibira") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<HibiraRunner>();
        }

        internal static void Unregister(HibiraClient client) => Clients.Remove(client);

        void Update()
        {
            foreach (var client in Clients.ToArray())
            {
                try
                {
                    client.Tick();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        void OnApplicationQuit()
        {
            foreach (var client in Clients.ToArray()) client.Dispose();
            Clients.Clear();
        }

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
#endif
