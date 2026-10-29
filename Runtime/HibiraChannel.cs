using System;
using System.Collections.Generic;

namespace Hibira
{
    /// <summary>A channel subscription made by <see cref="HibiraClient.Subscribe"/>.</summary>
    /// <remarks>
    /// Listen with <see cref="Bind"/> or <see cref="EventReceived"/>. Presence
    /// channels also keep <see cref="Members"/> and raise <see cref="MemberAdded"/>
    /// and <see cref="MemberRemoved"/>.
    /// </remarks>
    public sealed class HibiraChannel
    {
        /// <summary>
        /// The server accepted the subscription. On presence channels the data is
        /// the presence hash ({"ids", "hash", "count"}).
        /// </summary>
        public event Action<object> Subscribed;
        /// <summary>
        /// The subscription was refused or could not be signed: HTTP status from
        /// the auth endpoint (403 for a full room, say) or 0, and a message.
        /// </summary>
        public event Action<int, string> SubscriptionError;
        /// <summary>An event on this channel.</summary>
        public event Action<HibiraEvent> EventReceived;
        /// <summary>A presence member joined: id and user_info.</summary>
        public event Action<string, object> MemberAdded;
        /// <summary>A presence member left: id and the user_info they had.</summary>
        public event Action<string, object> MemberRemoved;
        /// <summary>Subscriber count, when subscription_count events are on for the app.</summary>
        public event Action<int> CountChanged;

        public string Name { get; }
        /// <summary>Your presence member id (from the auth endpoint's channel_data).</summary>
        public string MyId { get; private set; }
        /// <summary>Presence members: id → user_info.</summary>
        public IReadOnlyDictionary<string, object> Members => _members;
        /// <summary>True once the server has accepted the subscription (until a disconnect).</summary>
        public bool IsSubscribed { get; private set; }
        public bool IsPresence => Name.StartsWith("presence-", StringComparison.Ordinal);

        readonly WeakReference<HibiraClient> _client;
        readonly Dictionary<string, object> _members = new Dictionary<string, object>();
        readonly Dictionary<string, List<Action<HibiraEvent>>> _bindings = new Dictionary<string, List<Action<HibiraEvent>>>();

        internal HibiraChannel(HibiraClient client, string name)
        {
            _client = new WeakReference<HibiraClient>(client);
            Name = name;
        }

        internal HibiraClient Client => _client.TryGetTarget(out var c) ? c : null;

        /// <summary>
        /// Call <paramref name="callback"/> for every <paramref name="eventName"/> on
        /// this channel. "pusher:subscription_succeeded", "pusher:member_added" and
        /// "pusher:member_removed" work too, as in pusher-js.
        /// </summary>
        public void Bind(string eventName, Action<HibiraEvent> callback)
        {
            if (!_bindings.TryGetValue(eventName, out var list)) _bindings[eventName] = list = new List<Action<HibiraEvent>>();
            list.Add(callback);
        }

        /// <summary>Remove one callback, or every callback for <paramref name="eventName"/>.</summary>
        public void Unbind(string eventName, Action<HibiraEvent> callback = null)
        {
            if (!_bindings.TryGetValue(eventName, out var list)) return;
            if (callback == null) _bindings.Remove(eventName);
            else list.Remove(callback);
        }

        /// <summary>
        /// Send a client event to the other subscribers (not back to you). See
        /// <see cref="HibiraClient.Trigger"/>.
        /// </summary>
        public bool Trigger(string eventName, object data)
        {
            var client = Client;
            if (client == null || !IsSubscribed) return false;
            return client.Trigger(Name, eventName, data);
        }

        public void Unsubscribe() => Client?.Unsubscribe(Name);

        // ---- called by HibiraClient ------------------------------------------

        internal void SetChannelData(string channelData)
        {
            if (Json.TryParse(channelData, out var parsed) && Json.Get(parsed as Dictionary<string, object>, "user_id") is object id)
                MyId = Json.ToText(id);
        }

        internal void Reset()
        {
            IsSubscribed = false;
            _members.Clear();
        }

        internal void Failed(int status, string message) => SubscriptionError?.Invoke(status, message);

        internal void Handle(HibiraEvent ev)
        {
            var data = ev.Data as Dictionary<string, object>;
            switch (ev.Name)
            {
                case "pusher_internal:subscription_succeeded":
                {
                    IsSubscribed = true;
                    _members.Clear();
                    object payload = ev.Data;
                    if (IsPresence && Json.Get(data, "presence") is Dictionary<string, object> presence)
                    {
                        if (Json.Get(presence, "hash") is Dictionary<string, object> hash)
                            foreach (var kv in hash) _members[kv.Key] = kv.Value;
                        payload = presence;
                    }
                    Subscribed?.Invoke(payload);
                    Dispatch(new HibiraEvent(Name, "pusher:subscription_succeeded", payload, Json.Serialize(payload), null));
                    return;
                }
                case "pusher_internal:member_added":
                {
                    if (!(Json.Get(data, "user_id") is object rawId)) return;
                    var id = Json.ToText(rawId);
                    var info = Json.Get(data, "user_info");
                    var known = _members.ContainsKey(id);
                    _members[id] = info;
                    if (known) return;
                    MemberAdded?.Invoke(id, info);
                    Dispatch(MemberEvent("pusher:member_added", id, info));
                    return;
                }
                case "pusher_internal:member_removed":
                {
                    if (!(Json.Get(data, "user_id") is object rawId)) return;
                    var id = Json.ToText(rawId);
                    if (!_members.TryGetValue(id, out var info)) return;
                    _members.Remove(id);
                    MemberRemoved?.Invoke(id, info);
                    Dispatch(MemberEvent("pusher:member_removed", id, info));
                    return;
                }
                case "pusher_internal:subscription_count":
                    if (Json.IsNumber(Json.Get(data, "subscription_count")))
                        CountChanged?.Invoke((int)Json.ToDouble(data["subscription_count"]));
                    return;
            }
            if (ev.Name.StartsWith("pusher_internal:", StringComparison.Ordinal)) return;
            EventReceived?.Invoke(ev);
            Dispatch(ev);
        }

        HibiraEvent MemberEvent(string name, string id, object info)
        {
            var payload = new Dictionary<string, object> { ["id"] = id, ["info"] = info };
            return new HibiraEvent(Name, name, payload, Json.Serialize(payload), null);
        }

        void Dispatch(HibiraEvent ev)
        {
            if (!_bindings.TryGetValue(ev.Name, out var list)) return;
            foreach (var cb in list.ToArray()) cb(ev);
        }
    }
}
