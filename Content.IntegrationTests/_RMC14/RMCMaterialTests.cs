#nullable enable
using Content.IntegrationTests.Pair;
using Content.IntegrationTests.Utility;
using Content.Server.Construction.Components;
using Content.Server.Destructible;
using Content.Server.Destructible.Thresholds.Behaviors;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Construction.Steps;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Events;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Utility;
using System.Collections.Generic;
using System.Linq;

namespace Content.IntegrationTests._RMC14;

[TestFixture, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class RMCMaterialTests
{
    private static readonly ResPath[] RMCMapFiles = GameDataScrounger.FilesInDirectoryInVfs("/Maps/_RMC14", "*.yml");

    // Dictionary of entities which shouldn't appear on RMC maps, and the entity which should be used as a replacement (if any).
    // Todo: Move this to a YAML file?
    private static readonly Dictionary<EntProtoId, EntProtoId?> EntityBlacklist = new()
    {
        { "SheetSteel", "CMSheetMetal" },
        { "SheetSteel1", "CMSheetMetal1" },
        { "SheetSteel10", "CMSheetMetal10" },
        { "SheetSteelLingering0", null },
        { "SheetPlasteel", "CMSheetPlasteel" },
        { "SheetPlasteel1", "CMSheetPlasteel1" },
        { "SheetPlasteel10", "CMSheetPlasteel10" },
        { "SheetPlasteelLingering0", null },
        { "SheetGlass", "CMSheetGlass" },
        { "SheetGlass1", "CMSheetGlass1" },
        { "SheetGlass10", "CMSheetGlass10" },
        { "SheetGlassLingering0", null },
        { "SheetRGlass", "CMSheetGlassReinforced" },
        { "SheetRGlass1", "CMSheetGlassReinforced1" },
        { "SheetRGlassLingering0", null },
        { "SheetPGlass", "CMSheetGlassPhoron" },
        { "SheetPGlass1", "CMSheetGlassPhoron1" },
        { "SheetRPGlass", "CMSheetGlassPhoronReinforced" },
        { "SheetRPGlass1", "CMSheetGlassPhoronReinforced1" },
        { "SheetRPGlassLingering0", null },
        { "SheetPlasma", "CMSheetPhoron" },
        { "SheetPlasma1", "CMSheetPhoron1" },
        { "SheetPlasma10", "CMSheetPhoron10" },
        { "SheetPlasmaLingering0", null },
        { "SheetPlastic", "RMCSheetPlastic" },
        { "SheetPlastic1", "RMCSheetPlastic1" },
        { "SheetPlastic10", "RMCSheetPlastic10" },
        { "MaterialCardboard", "RMCSheetCardboard" },
        { "MaterialCardboard1", "RMCSheetCardboard1" },
        { "MaterialCardboard10", "RMCSheetCardboard10" },
        { "MaterialWoodPlank", "RMCPlankWood" },
        { "MaterialWoodPlank1", "RMCPlankWood1" },
        { "MaterialWoodPlank10", "RMCPlankWood10" },
        { "PartRodMetal", "CMRodMetal" },
        { "PartRodMetal1", "CMRodMetal1" },
        { "PartRodMetal10", "CMRodMetal10" },
        { "PartRodMetalLingering0", null },
        /* Cosmetic only, so these can all stay for now. */
        //{ "ShardGlass", "CMShardGlass" },
        //{ "ShardGlassReinforced", null },
        //{ "ShardGlassPlasma", "CMShardPhoron" },
        //{ "GoldOre", "RMCGoldOre" },
        //{ "GoldOre1", "RMCGoldOre1" },
        //{ "DiamondOre", "RMCDiamondOre" },
        //{ "DiamondOre1", "RMCDiamondOre1" },
        //{ "SteelOre", "RMCIronOre" },
        //{ "SteelOre1", "RMCIronOre1" },
        //{ "PlasmaOre", "RMCPlasmaOre" },
        //{ "PlasmaOre1", "RMCPlasmaOre1" },
        //{ "SilverOre", "RMCSilverOre" },
        //{ "SilverOre1", "RMCSilverOre1" },
        //{ "UraniumOre", "RMCUraniumOre" },
        //{ "UraniumOre1", "RMCUraniumOre1" },
        //{ "Coal", "RMCCoal" },
        //{ "Coal1", "RMCCoal1" },
        //{ "Coal5", null },
        //{ "Coal10", null },
        //{ "Coal15", null },
    };

    private static readonly Dictionary<ProtoId<ConstructionGraphPrototype>, HashSet<EntProtoId>> CheckedConstructionGraphs = [];

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
        var entSysManager = server.ResolveDependency<IEntitySystemManager>();
        var compFactory = server.ResolveDependency<IComponentFactory>();

        MappingDataNode yamlRoot;
        using (var reader = resourceManager.ContentFileReadText(mapFile))
        {
            yamlRoot = (MappingDataNode)DataNodeParser.ParseYamlStream(reader).First().Root;
        }

        // Pretend that the map is actually being loaded to get map migration data.
        var ev = new BeforeEntityReadEvent();
        server.EntMan.EventBus.RaiseEvent(EventSource.Local, ev);

        var deserializer = new EntityDeserializer(
            entSysManager.DependencyCollection,
            yamlRoot,
            DeserializationOptions.Default,
            ev.RenamedPrototypes,
            ev.DeletedPrototypes);

        Assert.That(deserializer.TryProcessData(), Is.True, $"Failed to deserialize {mapFile}");

        server.Log.Info($"Checking {deserializer.Prototypes.Count} prototypes...");
        using (Assert.EnterMultipleScope())
        {
            foreach (var (protoId, entityData) in deserializer.Prototypes)
            {
                var occurrences = entityData.Count;

                if (!server.ProtoMan.TryIndex(protoId, out var proto, false))
                    continue;

                DestructibleComponent? destructible = default!;
                ConstructionComponent? construction = default!;
                await server.WaitPost(() =>
                {
                    proto.TryGetComponent(out destructible, compFactory);
                    proto.TryGetComponent(out construction, compFactory);
                });

                // Check `DestructibleComponent` entity spawns for blacklisted entities.
                if (destructible is not null)
                {
                    foreach (var spawnedEnt in await CheckDestructibleSpawns(destructible))
                    {
                        var message = $"(DestructibleComponent) '{protoId}' ({occurrences} occurrences) spawns a '{spawnedEnt}' when destroyed, which should be inaccessible on RMC.";
                        if (EntityBlacklist[spawnedEnt] is { } suggestedReplacement)
                            message += $" (Try using '{suggestedReplacement}')";

                        Assert.Fail(message);
                    }
                }

                // Check `ConstructionComponent` graphs for blacklisted entities.
                if (construction is not null)
                {
                    foreach (var material in await CheckConstructionGraph(construction.Graph))
                    {
                        var message = $"(ConstructionComponent) '{protoId}' ({occurrences} occurrences) contains '{material}' in " +
                                            $"its construction graph ('{construction.Graph}'), which should be inaccessible on RMC.";
                        if (EntityBlacklist[material] is { } suggestedReplacement)
                            message += $" (Try using '{suggestedReplacement}')";

                        Assert.Fail(message);
                    }
                }
            }
        }
    }

    private static async Task<IEnumerable<EntProtoId>> CheckDestructibleSpawns(DestructibleComponent destructible)
    {
        var spawnedOnDestruction = destructible.Thresholds
            .SelectMany(t => t.Behaviors)
            .OfType<SpawnEntitiesBehavior>()
            .SelectMany(s => s.Spawn.Keys)
            .Intersect(EntityBlacklist.Keys);

        return spawnedOnDestruction;
    }

    private async Task<IEnumerable<EntProtoId>> CheckConstructionGraph(ProtoId<ConstructionGraphPrototype> graphProtoId)
    {
        // If this graph prototype has already been checked previously.
        if (CheckedConstructionGraphs.TryGetValue(graphProtoId, out var graphMaterials))
            return graphMaterials;

        var graph = _pair.Server.ProtoMan.Index(graphProtoId);

        var invalidMaterials = graph.Nodes.Values
            .SelectMany(node => node.Edges)
            .SelectMany(edge => edge.Steps)
            .OfType<MaterialConstructionGraphStep>()
            .Select(step => _pair.Server.ProtoMan.Index(step.MaterialPrototypeId).Spawn)
            .Where(proto => EntityBlacklist.Keys.Contains(proto))
            .ToHashSet();

        CheckedConstructionGraphs.Add(graphProtoId, invalidMaterials);
        return invalidMaterials;
    }
}
