using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS.Json
{
    public sealed class DataAssetContractResolver : DefaultContractResolver
    {
        protected override List<MemberInfo> GetSerializableMembers(Type objectType)
        {
            var members = base.GetSerializableMembers(objectType);

            if (!typeof(IDataAsset).IsAssignableFrom(objectType))
                return members;

            return members.Where(m =>
                m.Name == nameof(IDataAsset.AssetId) ||
                m.GetCustomAttribute<KlothoOrderAttribute>() != null
            ).ToList();
        }

        protected override JsonObjectContract CreateObjectContract(Type objectType)
        {
            var contract = base.CreateObjectContract(objectType);
            if (!typeof(IDataAsset).IsAssignableFrom(objectType))
                return contract;

            // Route deserialization through ctor(int assetId) so the generator-emitted
            // parameterless ctor (single-instance assets) cannot bypass AssetId (which is
            // now a readonly backing field — no setter). base.CreateObjectContract prefers
            // the parameterless ctor when present, leaving CreatorParameters empty — that
            // mismatches our ctor(int) invocation, so we set CreatorParameters explicitly.
            var ctorInt = objectType.GetConstructor(new[] { typeof(int) });
            if (ctorInt != null)
            {
                contract.OverrideCreator = args => ctorInt.Invoke(args);
                contract.CreatorParameters.Clear();
                contract.CreatorParameters.Add(new JsonProperty
                {
                    PropertyName = nameof(IDataAsset.AssetId),
                    PropertyType = typeof(int),
                    Required = Required.Always,
                });
            }
            return contract;
        }
    }
}
