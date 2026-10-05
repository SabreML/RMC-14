#nullable enable
using Content.IntegrationTests.Pair;
using Content.IntegrationTests.Utility;
using Content.Server.Construction.Components;
using Content.Server.Destructible;
using Content.Server.Destructible.Thresholds.Behaviors;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Construction.Steps;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using System.Collections.Generic;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace Content.IntegrationTests._RMC14;

[TestFixture, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class RMCMaterialTests
{
    private static readonly ResPath[] RMCMapFiles = GameDataScrounger.FilesInDirectoryInVfs("/Maps/_RMC14", "*.yml");

    private static readonly EntProtoId[] EntityBlacklist = [
        "SheetSteel",
        "SheetSteel1",
        "SheetSteel10",
    ];

    private TestPair _pair = default!;

    [TearDown]
    public async Task TearDown()
    {
        await _pair.CleanReturnAsync();
    }

    [Test]
    [TestCaseSource(nameof(RMCMapFiles))]
    public async Task CheckMapsForIncorrectEntityDrops(ResPath mapFile)
    {
        _pair = await PoolManager.GetServerClient();
        var server = _pair.Server;

        var resourceManager = server.ResolveDependency<IResourceManager>();
        var compFactory = server.ResolveDependency<IComponentFactory>();

        var yamlStream = resourceManager.ContentFileReadYaml(mapFile);
        var root = (YamlMappingNode)yamlStream.Documents[0].RootNode;
        var mapEntities = (YamlSequenceNode)root["entities"];

        server.Log.Info($"Checking {mapEntities.Count()} entities...");
        using (Assert.EnterMultipleScope())
        {
            foreach (var entity in mapEntities.Cast<YamlMappingNode>())
            {
                var protoId = entity.GetNode<YamlScalarNode>("proto").Value!;
                var occurrences = entity.GetNode<YamlSequenceNode>("entities").Count();

                if (!server.ProtoMan.TryIndex(protoId, out var proto, false))
                    continue;

                foreach (var spawnedEnt in await CheckDestructibleSpawns(proto, compFactory))
                    Assert.Fail($"The '{proto.ID}' entity ({occurrences} occurrences) spawns a '{spawnedEnt}' when destroyed, which should be inaccessible on RMC.");

                foreach (var graphMaterial in await CheckConstructionGraph(proto, compFactory))
                    Assert.Fail($"The '{proto.ID}' entity ({occurrences} occurrences) contains {graphMaterial} in its construction graph, which should be inaccessible on RMC.");
            }
        }
    }

    private async Task<IEnumerable<EntProtoId>> CheckDestructibleSpawns(EntityPrototype proto, IComponentFactory compFactory)
    {
        DestructibleComponent? destructible = default!;
        await _pair.Server.WaitPost(() => proto.TryGetComponent(out destructible, compFactory));
        if (destructible is null)
            return [];

        var spawnedOnDestruction = destructible.Thresholds
            .SelectMany(t => t.Behaviors)
            .OfType<SpawnEntitiesBehavior>()
            .SelectMany(s => s.Spawn.Keys);

        return spawnedOnDestruction.Intersect(EntityBlacklist);
    }

    private async Task<EntProtoId[]> CheckConstructionGraph(EntityPrototype proto, IComponentFactory compFactory)
    {
        ConstructionComponent? construction = default!;
        await _pair.Server.WaitPost(() => proto.TryGetComponent(out construction, compFactory));
        if (construction is null)
            return [];

        if (!_pair.Server.ProtoMan.TryIndex<ConstructionGraphPrototype>(construction.Graph, out var graph))
            return [];

        var invalidMaterials = new HashSet<EntProtoId>();
        foreach (var (_, graphNode) in graph.Nodes)
        {
            foreach (var edge in graphNode.Edges)
            {
                foreach (var materialStep in edge.Steps.OfType<MaterialConstructionGraphStep>())
                {
                    if (!_pair.Server.ProtoMan.TryIndex(materialStep.MaterialPrototypeId, out var material))
                        continue;
                    if (EntityBlacklist.Contains(material.Spawn))
                        invalidMaterials.Add(material.Spawn);
                }
            }
        }

        return invalidMaterials.ToArray();
    }
}
