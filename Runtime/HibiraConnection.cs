#if UNITY_5_3_OR_NEWER
using UnityEngine;

namespace Hibira
{
    /// <summary>
    /// A <see cref="HibiraClient"/> set up in the Inspector. Add it to a
    /// GameObject, fill in the app key, and use <see cref="Client"/> from your
    /// own scripts.
    /// </summary>
    [AddComponentMenu("Hibira/Hibira Connection")]
    [DisallowMultipleComponent]
    public sealed class HibiraConnection : MonoBehaviour
    {
        [Tooltip("The app key from the Hibira portal. Never put the app secret in a game.")]
        public string appKey = "";
        [Tooltip("WebSocket host. Hibira: ws.hibira.io")]
        public string host = "ws.hibira.io";
        public int port = 443;
        public bool useTls = true;
        [Tooltip("Your backend URL that signs private and presence subscriptions (the same one pusher-js would use).")]
        public string authEndpoint = "";
        [Tooltip("Connect in Start().")]
        public bool connectOnStart = true;
        [Tooltip("Keep this object (and the connection) when a new scene loads.")]
        public bool dontDestroyOnLoad = true;

        HibiraClient _client;

        /// <summary>The client, made on first use from the Inspector fields.</summary>
        public HibiraClient Client
        {
            get
            {
                if (_client == null)
                {
                    _client = new HibiraClient(new HibiraOptions
                    {
                        AppKey = appKey,
                        Host = host,
                        Port = port,
                        UseTls = useTls,
                        AuthEndpoint = authEndpoint,
                    });
                }
                return _client;
            }
        }

        void Awake()
        {
            if (dontDestroyOnLoad) DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            if (!connectOnStart) return;
            if (string.IsNullOrEmpty(appKey))
            {
                Debug.LogWarning("HibiraConnection: set the app key in the Inspector", this);
                return;
            }
            Client.Connect();
        }

        void OnDestroy()
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
#endif
