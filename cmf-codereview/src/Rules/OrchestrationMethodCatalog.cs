namespace Cmf.Cli.Plugin.CodeReview.Rules;

/// <summary>
/// Orchestration-layer methods with known per-call cost, audited from the CM Framework platform
/// source (the I*Orchestration interfaces under MES/Host/Biz/Orchestration/Abstractions --
/// IMaterialOrchestration, IContainerOrchestration, IResourceOrchestration, IProductOrchestration).
/// Two groups:
/// <list type="bullet">
/// <item><see cref="BulkSiblingBySingular"/> -- per-item methods with a CONFIRMED bulk sibling on
/// the same interface (TrackInMaterial/TrackInMaterials). Calling the singular in a loop has a
/// concrete, named fix: call the plural once. Every pair was verified by finding both signatures
/// on the interface, with the plural taking a collection of the singular's subject.</item>
/// <item><see cref="NoBulkMethodNames"/> -- methods with real per-call DB/service cost and NO bulk
/// alternative at the platform level (verified: no plural sibling exists on the interface). The
/// honest advice is to reduce iteration count; suggesting a bulk method would point at something
/// that doesn't exist.</item>
/// </list>
/// Matching is name-based and additionally requires the receiver expression to contain
/// "orchestration" (case-insensitive) -- the proxy field/property naming convention
/// (_materialOrchestration, ContainerOrchestration, ...) -- so unrelated same-named methods on
/// other types don't match.
/// </summary>
public static class OrchestrationMethodCatalog
{
    /// <summary>Singular method -> its confirmed bulk sibling on the same orchestration interface.</summary>
    public static readonly IReadOnlyDictionary<string, string> BulkSiblingBySingular = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // IMaterialOrchestration -- command/write pairs
        ["ProcessDispensedMaterial"] = "ProcessDispensedMaterials",
        ["ChangeMaterialProduct"] = "ChangeMaterialsProduct",
        ["SpecialChangeMaterialProduct"] = "SpecialChangeMaterialsProduct",
        ["ChangeMaterialFlowAndStep"] = "ChangeMaterialsFlowAndStep",
        ["ChangeMaterialQuantity"] = "ChangeMaterialsQuantity",
        ["ChangeMaterialType"] = "ChangeMaterialsType",
        ["DispatchMaterial"] = "DispatchMaterials",
        ["TrackInMaterial"] = "TrackInMaterials",
        ["TrackOutMaterial"] = "TrackOutMaterials",
        ["AbortMaterialProcess"] = "AbortMaterialsProcess",
        ["UndispatchMaterial"] = "UndispatchMaterials",
        ["MoveMaterialToNextStep"] = "MoveMaterialsToNextStep",
        ["ComplexMoveMaterialToNextStep"] = "ComplexMoveMaterialsToNextStep",
        ["ComplexTrackInMaterial"] = "ComplexTrackInMaterials",
        ["ComplexTrackOutMaterial"] = "ComplexTrackOutMaterials",
        ["ComplexTrackOutAndMoveMaterialToNextStep"] = "ComplexTrackOutAndMoveMaterialsToNextStep",
        ["ComplexDispatchAndTrackInMaterial"] = "ComplexDispatchAndTrackInMaterials",
        ["ReworkMaterial"] = "ReworkMaterials",
        ["ComplexReworkMaterial"] = "ComplexReworkMaterials",
        ["SetOrUnSetMaterialDispatchable"] = "SetOrUnSetMaterialsDispatchable",
        ["PerformMaterialChecklistItem"] = "PerformMaterialChecklistItems",
        ["CreateMaterial"] = "CreateMaterials",
        ["TerminateMaterial"] = "TerminateMaterials",
        ["HoldMaterial"] = "HoldMaterials",
        ["ReleaseMaterial"] = "ReleaseMaterials",
        ["StoreMaterial"] = "StoreMaterials",
        ["RetrieveMaterial"] = "RetrieveMaterials",
        ["RecordMaterialLoss"] = "RecordMaterialsLoss",
        ["RecordMaterialBonus"] = "RecordMaterialsBonus",

        // IMaterialOrchestration -- read/query pairs (same per-call service cost, bulk read exists)
        ["LoadContainersForMaterial"] = "LoadContainersForMaterials",
        ["GetNextStepsForMaterialInFacility"] = "GetNextStepsForMaterialsInFacility",
        ["GetMaterialBasicInformation"] = "GetMaterialsBasicInformation",
        ["LoadMaterialTopMostMaterial"] = "LoadMaterialsTopMostMaterial",

        // IContainerOrchestration
        ["CreateContainer"] = "CreateContainers",
        ["StoreContainer"] = "StoreContainers",
        ["RetrieveContainer"] = "RetrieveContainers",

        // IResourceOrchestration
        ["CreateResource"] = "CreateResources",
        ["AdjustResourceState"] = "AdjustResourcesState",
        ["AttachConsumableToResource"] = "AttachConsumablesToResource",
        ["DetachConsumableFromResource"] = "DetachConsumablesFromResource",
    };

    /// <summary>Orchestration methods with confirmed per-call cost and confirmed NO bulk alternative on their interface.</summary>
    public static readonly HashSet<string> NoBulkMethodNames = new(StringComparer.Ordinal)
    {
        // IMaterialOrchestration
        "MoveMaterialToStep",
        "ComplexMoveMaterialToStep",
        "GradeMaterial",
        "UnterminateMaterial", // asymmetric: TerminateMaterial HAS a bulk sibling, Unterminate doesn't
        "UpdateMaterial",
        // IContainerOrchestration
        "TerminateContainer",
        "UnterminateContainer",
        "DockContainer",
        "UndockContainer",
        "EmptyContainer",
        "UpdateContainer",
        // IResourceOrchestration
        "CreateResourceJob",
        "UpdateResource",
        "TerminateResource",
        "UnterminateResource",
        // IProductOrchestration
        "BlockProduct",
        "UnblockProduct",
    };

    /// <summary>True when the receiver expression text looks like an orchestration proxy (_materialOrchestration, ContainerOrchestration, ...).</summary>
    public static bool IsOrchestrationReceiver(string? receiverText) =>
        receiverText?.Contains("rchestration", StringComparison.OrdinalIgnoreCase) == true;
}
