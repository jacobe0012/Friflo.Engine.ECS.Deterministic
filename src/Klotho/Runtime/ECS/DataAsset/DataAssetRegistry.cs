using System;
using System.Collections.Generic;
using System.Reflection;

namespace xpTURN.Klotho.ECS
{
    public sealed class DataAssetRegistry : IDataAssetRegistryBuilder
    {
        private readonly Dictionary<int, IDataAsset> _assets = new Dictionary<int, IDataAsset>();
        private readonly Dictionary<(Type, string), IDataAsset> _byKey = new Dictionary<(Type, string), IDataAsset>();
        private bool _locked;

        public void Register(IDataAsset asset)
        {
            if (_locked)
                throw new InvalidOperationException("DataAssetRegistry is locked after simulation start.");
            if (_assets.ContainsKey(asset.AssetId))
                throw new InvalidOperationException($"Duplicate AssetId: {asset.AssetId}");
            _assets[asset.AssetId] = asset;

            IndexByAttributeKey(asset);
        }

        public void RegisterRange(IReadOnlyList<IDataAsset> assets)
        {
            for (int i = 0; i < assets.Count; i++)
                Register(assets[i]);
        }

        private void IndexByAttributeKey(IDataAsset asset)
        {
            // Read [KlothoDataAsset(Key = "...")] via System.Reflection.CustomAttributeData
            // so the actual presence of the named arg is preserved (vs the materialized
            // attribute instance, which cannot distinguish a missing Key from a null one).
            var type = asset.GetType();
            foreach (var data in CustomAttributeData.GetCustomAttributes(type))
            {
                if (data.AttributeType != typeof(KlothoDataAssetAttribute)) continue;
                foreach (var arg in data.NamedArguments)
                {
                    if (arg.MemberName != "Key") continue;
                    if (!(arg.TypedValue.Value is string key) || string.IsNullOrEmpty(key))
                        continue;

                    var lookup = (type, key);
                    if (_byKey.ContainsKey(lookup))
                        throw new InvalidOperationException(
                            $"Duplicate DataAsset Key: type={type.Name}, key={key}");
                    _byKey[lookup] = asset;
                }
                break;
            }
        }

        private void Lock() => _locked = true;

        IDataAssetRegistry IDataAssetRegistryBuilder.Build()
        {
            Lock();
            return this;
        }

        public T Get<T>(int id) where T : IDataAsset
        {
            if (!_assets.TryGetValue(id, out var asset))
                throw new KeyNotFoundException($"DataAsset not found: id={id}, type={typeof(T).Name}");
            return (T)asset;
        }

        public bool TryGet<T>(int id, out T result) where T : IDataAsset
        {
            if (_assets.TryGetValue(id, out var asset) && asset is T typed)
            {
                result = typed;
                return true;
            }
            result = default;
            return false;
        }

        public T Get<T>(DataAssetRef assetRef) where T : IDataAsset
            => Get<T>(assetRef.Id);

        public bool TryGet<T>(DataAssetRef assetRef, out T result) where T : IDataAsset
            => TryGet(assetRef.Id, out result);

        public T Get<T>() where T : IDataAsset
        {
            if (!AttributeCache<T>.AssetId.HasValue)
                throw new InvalidOperationException(
                    $"Get<{typeof(T).Name}>() requires [KlothoDataAsset(AssetId = ...)]. " +
                    $"Use Get<{typeof(T).Name}>(int id) for multi-instance assets.");
            return Get<T>(AttributeCache<T>.AssetId.Value);
        }

        public bool TryGet<T>(out T result) where T : IDataAsset
        {
            if (!AttributeCache<T>.AssetId.HasValue)
            {
                result = default;
                return false;
            }
            return TryGet(AttributeCache<T>.AssetId.Value, out result);
        }

        public T GetByKey<T>(string key) where T : IDataAsset
        {
            if (!_byKey.TryGetValue((typeof(T), key), out var asset))
                throw new KeyNotFoundException(
                    $"GetByKey<{typeof(T).Name}>(\"{key}\") — no asset registered with this (Type, Key).");
            return (T)asset;
        }

        public bool TryGetByKey<T>(string key, out T result) where T : IDataAsset
        {
            if (_byKey.TryGetValue((typeof(T), key), out var asset) && asset is T typed)
            {
                result = typed;
                return true;
            }
            result = default;
            return false;
        }

        private static class AttributeCache<T> where T : IDataAsset
        {
            public static readonly int? AssetId;
            public static readonly string Key;

            static AttributeCache()
            {
                foreach (var data in CustomAttributeData.GetCustomAttributes(typeof(T)))
                {
                    if (data.AttributeType != typeof(KlothoDataAssetAttribute)) continue;
                    foreach (var arg in data.NamedArguments)
                    {
                        if (arg.MemberName == "AssetId" && arg.TypedValue.Value is int aid)
                            AssetId = aid;
                        else if (arg.MemberName == "Key" && arg.TypedValue.Value is string k)
                            Key = k;
                    }
                    break;
                }
            }
        }
    }
}
