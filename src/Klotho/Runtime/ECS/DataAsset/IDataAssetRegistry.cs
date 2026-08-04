namespace xpTURN.Klotho.ECS
{
    public interface IDataAssetRegistry
    {
        T Get<T>(int id) where T : IDataAsset;
        bool TryGet<T>(int id, out T result) where T : IDataAsset;
        T Get<T>(DataAssetRef assetRef) where T : IDataAsset;
        bool TryGet<T>(DataAssetRef assetRef, out T result) where T : IDataAsset;

        /// <summary>
        /// Returns the single instance whose AssetId matches <c>[KlothoDataAsset(AssetId = ...)]</c>
        /// declared on type <typeparamref name="T"/>. Throws when the attribute lacks AssetId
        /// or no entry is registered for that id.
        /// </summary>
        T Get<T>() where T : IDataAsset;
        bool TryGet<T>(out T result) where T : IDataAsset;

        /// <summary>
        /// Returns the instance whose <c>[KlothoDataAsset(Key = "...")]</c> matches the given key.
        /// Lookup key is (typeof(T), key) — concrete type match required.
        /// </summary>
        T GetByKey<T>(string key) where T : IDataAsset;
        bool TryGetByKey<T>(string key, out T result) where T : IDataAsset;
    }
}
