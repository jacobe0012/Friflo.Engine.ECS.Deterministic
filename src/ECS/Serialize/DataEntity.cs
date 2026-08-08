// Copyright (c) Ullrich Praetz - https://github.com/friflo. All rights reserved.
// See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
#if !UNITY_2018_1_OR_NEWER
using System.ComponentModel.DataAnnotations;    // [Key] — see DataEntity.pid
#endif
using Friflo.Json.Fliox;

// ReSharper disable ClassNeverInstantiated.Global
namespace Friflo.Engine.ECS.Serialize;

/// <summary>
/// A <see cref="DataEntity"/> represents an <see cref="Entity"/> as a POCO class used for serialization.
/// </summary>
/// <remarks>
/// When reading / writing <see cref="DataEntity"/>'s in the editor a special MemoryDatabase" implementation is required.<br/>
/// This implementation preserves the order of entities stored in a JSON file.<br/>
/// Therefor it stores the order of each entity when loaded and apply this order when writing them back to the JSON file.<br/> 
/// <br/>
/// Having a stable order is required avoid merge conflicts.<br/>
/// Otherwise the entity order in a JSON file would be arbitrary.<br/>
/// Even small changes will show a massive diff in version control. 
/// </remarks>
[CLSCompliant(true)]
public sealed class DataEntity
{
    /// <summary>Permanent id used to identify entities in a database</summary>
    /// <remarks>
    /// [Key] marks this as the primary key for a Fliox database (EntityStore itself never reads
    /// it). It comes from System.ComponentModel.DataAnnotations, which is absent from the
    /// netstandard2.1 profile Unity compiles against — Unity resolves the name to its own
    /// System.ComponentModel.Annotations facade, which forwards to mscorlib and does not carry
    /// KeyAttribute, so shipping the real NuGet package alongside does not win. Omitted under
    /// Unity: nothing there hosts a Fliox database, and [Serialize("id")] — which does affect the
    /// wire format — is unconditional.
    /// </remarks>
#if !UNITY_2018_1_OR_NEWER
    [Key]
#endif
    [Serialize            ("id")]
    public  long            pid;        //  8
    
    /// <summary>
    /// The list of child entity ids. 
    /// </summary>
    /// <remarks>
    /// Used a list of child ids instead of a single field <c>parentId</c> to enable child order.<br/>
    /// <br/>
    /// An alternative order implementation - using firstChild, nextSibling - is error prone if referenced nodes are missing.<br/>
    /// For now the child order is required to enable a memorable order in the editor and to avoid merge conflicts. 
    /// </remarks>
    public  List<long>      children;   //  8   - can be null
    
    /// <summary>
    /// Each key in <see cref="components"/> defines the type of a component or script.<br/>
    /// Its value is the component / script value.
    /// </summary>
    public  JsonValue       components; // 16   - can be null
    
    /// <summary>List of tags assigned to an entity</summary>
    public  List<string>    tags;       //  8   - can be null
    
    /// <summary>if != null the entity is the root of a scene using the assigned <see cref="sceneName"/></summary>
    public  string          sceneName;  //  8   - can be null
    
    /// <summary>Reference to the `Prefab` the entity is based on</summary>
    public  string          prefab;     //  8   - can be null
    
    /// <summary>
    /// Modify the referenced entity of a`preFab`.<br/> with <see cref="components"/> != null<br/>
    /// Remove the referenced entity if <see cref="components"/> == null
    /// </summary>
    public  string          modify;     //  8   - can be null

    public  override string ToString()  => $"pid: {pid}";
    
    /// <summary> Return the <b>JSON</b> representation of a <see cref="DataEntity"/>. </summary>
    /// <remarks> Counterpart of <see cref="Entity.DebugJSON"/> </remarks>
    public  string          DebugJSON   => EntityUtils.DataEntityToJSON(this);
}
