namespace Hibira
{
    /// <summary>An event received on a channel.</summary>
    public sealed class HibiraEvent
    {
        public string Channel { get; }
        public string Name { get; }
        /// <summary>
        /// The payload, decoded: Dictionary&lt;string, object&gt;, List&lt;object&gt;,
        /// long, double, string, bool or null. See <see cref="Json"/>.
        /// </summary>
        public object Data { get; }
        /// <summary>The payload as JSON text.</summary>
        public string DataJson { get; }
        /// <summary>
        /// Who sent a client event on a presence channel. The server stamps it,
        /// so it cannot be faked. Null otherwise.
        /// </summary>
        public string UserId { get; }

        public HibiraEvent(string channel, string name, object data, string dataJson, string userId)
        {
            Channel = channel;
            Name = name;
            Data = data;
            DataJson = dataJson;
            UserId = userId;
        }

        /// <summary>
        /// The payload as one of your own <c>[Serializable]</c> classes, read with
        /// Unity's <c>JsonUtility</c>.
        /// </summary>
        public T DataAs<T>()
        {
#if UNITY_5_3_OR_NEWER
            return UnityEngine.JsonUtility.FromJson<T>(DataJson);
#else
            throw new System.NotSupportedException("DataAs<T>() needs Unity's JsonUtility");
#endif
        }

        /// <summary>The payload's <paramref name="key"/> field, when the payload is an object.</summary>
        public object this[string key] => Json.Get(Data as System.Collections.Generic.Dictionary<string, object>, key);

        public override string ToString() => $"{Channel} {Name} {DataJson}";
    }
}
