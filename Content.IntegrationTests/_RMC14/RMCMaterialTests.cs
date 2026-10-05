#nullable enable
using Content.IntegrationTests.Pair;
using Content.IntegrationTests.Utility;
using Content.Server.Construction;
using Content.Server.Construction.Completions;
using Content.Server.Construction.Components;
using Content.Server.Destructible;
using Content.Server.Destructible.Thresholds.Behaviors;
using Content.Shared.Construction;
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

    // Dictionary of prototypes which shouldn't appear on RMC maps, and the prototype that should be used as a replacement (if any).
    // Todo: Move this to a YAML file?
    private static readonly Dictionary<ProtoId<IPrototype>, ProtoId<IPrototype>?> PrototypeBlacklist = new()
    {
        // ProtoID<StackPrototype>:
        { "Steel", "CMSteel" },
        { "Plasteel", "CMPlasteel" },
        { "Glass", "CMGlass" },
        { "ReinforcedGlass", "CMGlassReinforced" },
        { "PlasmaGlass", "CMGlassPhoron" },
        { "ReinforcedPlasmaGlass", "CMGlassPhoronReinforced" },
        { "Plasma", "CMPhoron" },
        { "Plastic", "RMCPlastic" },
        { "Cardboard", "RMCSheetCardboard" },
        { "WoodPlank", "RMCWood" },
        { "MetalRod", "CMRodMetal" },
        // EntProtoId:
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

    // {String of "graphId-nodeName": HashSet of blacklisted prototypes that are spawned/used by the node (or empty if none)}
    private static readonly Dictionary<string, HashSet<ProtoId<IPrototype>>> CheckedConstructionNodes = [];

    private TestPair _pair = default!;

    [TearDown]
    public async Task TearDown()
    {
        await _pair.CleanReturnAsync();
    }

    // todo: separate test going through every prototype for entries in the openable construction menu, and running the same entity checks

    [Test]
    [TestCaseSource(nameof(RMCMapFiles))]
    public async Task CheckMapsForNonRMCPrototypes(ResPath mapFile)
    {
        _pair = await PoolManager.GetServerClient();
        var server = _pair.Server;

        var resourceManager = server.ResolveDependency<IResourceManager>();
        var entSysManager = server.ResolveDependency<IEntitySystemManager>();
        var compFactory = server.ResolveDependency<IComponentFactory>();
        var constructionSys = server.System<ConstructionSystem>();

        // Properly loading a map takes forever and isn't actually necessary for this, so just search through the yaml files instead.
        MappingDataNode yamlRoot;
        using (var reader = resourceManager.ContentFileReadText(mapFile))
        {
            yamlRoot = (MappingDataNode)DataNodeParser.ParseYamlStream(reader).First().Root;
        }

        // Pretend that the map is being loaded to get map migration data. (renamed/deleted prototypes)
        var ev = new BeforeEntityReadEvent();
        server.EntMan.EventBus.RaiseEvent(EventSource.Local, ev);

        var deserializer = new EntityDeserializer(
            entSysManager.DependencyCollection,
            yamlRoot,
            DeserializationOptions.Default,
            ev.RenamedPrototypes,
            ev.DeletedPrototypes);

        Assert.That(deserializer.TryProcessData(), Is.True, $"Failed to deserialize {mapFile}!");

        server.Log.Info($"Checking {deserializer.Prototypes.Count} prototypes...");
        using (Assert.EnterMultipleScope())
        {
            foreach (var (protoId, entityData) in deserializer.Prototypes)
            {
                if (!server.ProtoMan.TryIndex(protoId, out var proto, false))
                    continue;

                DestructibleComponent? destructibleComp = default!;
                ConstructionComponent? constructionComp = default!;
                await server.WaitPost(() =>
                {
                    proto.TryGetComponent(out destructibleComp, compFactory);
                    proto.TryGetComponent(out constructionComp, compFactory);
                });

                // Check `DestructibleComponent` entity spawns for blacklisted prototypes.
                if (destructibleComp is not null)
                {
                    foreach (var blacklisted in CheckDestructibleSpawns(destructibleComp))
                    {
                        var message = $"(DestructibleComponent) '{protoId}' ({entityData.Count} occurrences) spawns a '{blacklisted}' when destroyed, which should be inaccessible on RMC.";
                        if (PrototypeBlacklist[blacklisted] is { } suggestedReplacement)
                            message += $" (Try using '{suggestedReplacement}')";

                        Assert.Fail(message);
                    }
                }

                // Check `ConstructionComponent` graphs for blacklisted prototypes, either used in construction or dropped by deconstruction.
                if (constructionComp is not null)
                {
                    foreach (var blacklisted in CheckConstructionNodeEdges(constructionComp, constructionSys))
                    {
                        var message = $"(ConstructionComponent) '{protoId}' ({entityData.Count} occurrences) contains '{blacklisted}' in " +
                                            $"its construction graph ('{constructionComp.Graph}'), which should be inaccessible on RMC.";
                        if (PrototypeBlacklist[blacklisted] is { } suggestedReplacement)
                            message += $" (Try using '{suggestedReplacement}')";

                        Assert.Fail(message);
                    }
                }
            }
        }
    }

    private static IEnumerable<ProtoId<IPrototype>> CheckDestructibleSpawns(DestructibleComponent destructible)
    {
        var spawnedOnDestruction = destructible.Thresholds
            .SelectMany(t => t.Behaviors)
            .OfType<SpawnEntitiesBehavior>()
            .SelectMany(s => s.Spawn.Keys)
            .Select(e => (ProtoId<IPrototype>)e.Id)
            .Intersect(PrototypeBlacklist.Keys);

        return spawnedOnDestruction;
    }

    private HashSet<ProtoId<IPrototype>> CheckConstructionNodeEdges(ConstructionComponent constructionComp, ConstructionSystem constructionSys)
    {
        // Check if this specific node in the graph has already been parsed previously. If so, just return the cached ProtoIds from that.
        var identifierString = $"{constructionComp.Graph}-{constructionComp.Node}";
        if (CheckedConstructionNodes.TryGetValue(identifierString, out var nodePrototypes))
            return nodePrototypes;

        // Get the overall construction graph and the entity's current "node" in that graph.
        var graph = _pair.Server.ProtoMan.Index<ConstructionGraphPrototype>(constructionComp.Graph);
        var startingNode = constructionSys.GetNodeFromGraph(graph, constructionComp.Node);
        Assert.That(startingNode, Is.Not.Null); // Should never be the case but may as well check.

        var blacklistedPrototypes = new HashSet<ProtoId<IPrototype>>();

        // Set up to semi-recursively loop through every "edge" of this node, then the "target" node of each edge, then the edges of *that* node, etc.
        var visitedNodes = new HashSet<string>() { startingNode.Name };
        var stack = new Stack<ConstructionGraphNode>();
        stack.Push(startingNode);
        while (stack.Count > 0)
        {
            var currentNode = stack.Pop();
            foreach (var edge in currentNode.Edges)
            {
                // Look for any prototypes that are required to perform one of the edge's steps.
                foreach (var materialStep in edge.Steps.OfType<MaterialConstructionGraphStep>())
                {
                    // Check the `StackPrototype`'s ID first, then the `EntProtoID` of the entity contained inside it.
                    var stackPrototype = _pair.Server.ProtoMan.Index(materialStep.MaterialPrototypeId);
                    var stackEntityId = stackPrototype.Spawn;
                    if (PrototypeBlacklist.ContainsKey(stackPrototype.ID))
                    {
                        blacklistedPrototypes.Add(stackPrototype.ID);
                    }
                    else if (PrototypeBlacklist.ContainsKey(stackEntityId.Id))
                    {
                        blacklistedPrototypes.Add(stackEntityId.Id);
                    }
                }

                // Look for any prototypes that are spawned when the edge's steps are completed.
                foreach (var action in edge.Completed)
                {
                    if (CheckAction(action) is { } spawnedPrototype && PrototypeBlacklist.ContainsKey(spawnedPrototype))
                        blacklistedPrototypes.Add(spawnedPrototype);
                }

                // If this edge has an unvisited target, add that to the top of the stack.
                if (constructionSys.GetNodeFromGraph(graph, edge.Target) is { } targetNode && visitedNodes.Add(edge.Target))
                    stack.Push(targetNode);
            }
        }

        // Add this node and the results to the "checked" list, so that all of the above can be skipped next time.
        CheckedConstructionNodes.Add(identifierString, blacklistedPrototypes);
        return blacklistedPrototypes;

        // defined over here as a local function so that it can call itself
        static ProtoId<IPrototype>? CheckAction(IGraphAction action)
        {
            return action switch
            {
                ConditionalAction conditional => CheckAction(conditional.Action!),
                GivePrototype give => give.Prototype.Id,
                SpawnPrototype spawn => spawn.Prototype,
                SpawnPrototypeAtContainer spawnatContainer => spawnatContainer.Prototype,
                _ => null
            };
        }
    }
}
