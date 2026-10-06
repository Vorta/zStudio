namespace Recoil.Zbd.Core.Worlds;

/// <summary>Retail 0x4C20A0's case-sensitive core command prefixes, in dispatch order.
/// Interpreter built-ins and GameGen-specific commands are handled separately; exact-only core names are not prefixes.</summary>
internal static class ScriptCommands
{
    private static readonly string[] Prefixes =
    [
        "AddChild", "AddEnhancerImage", "AnimSetZBDFile", "AnimSetDebugFrame",
        "CameraRotate", "CameraSetActive", "CameraSetDynamicLOD", "CameraSetFOV",
        "CameraSetHorizonXZ", "CameraSetHorizon", "CameraSetLODMultiplier", "CameraSetNearFarClip",
        "CameraSetObjectHSETest", "CameraSetWindow", "CameraSetWorld", "CameraTranslate",
        "CountUsedNodes", "CycleTextureSetLooping", "CycleTextureSetMap", "CycleTextureSetOn",
        "CycleTextureSetSpeed", "ClearScreenBuffer", "DeleteChild", "DeleteFile",
        "DeleteTree", "DisplayOrigin", "DisplayResolution", "DisplaySetClearColor",
        "echo", "Echo", "FindNode", "FindSubNode",
        "FreeNode", "GameZReadZBDFile", "GameZWriteZBDFile", "GetBFETolerance",
        "LensFlareTexture", "LightNew", "LightSetActive", "LightSetAmbient",
        "LightSetColor", "LightSetDiffuse", "LightSetDirectedSource", "LightSetDirectional",
        "LightSetOrientation", "LightSetPointSource", "LightSetRanges", "LightSetTranslate",
        "LoadSoils", "LODAddChild", "LODSetRange", "MatlFaceColor",
        "MatlNew", "MatlTexture", "ModelNew", "ModelPolygonBegin",
        "ModelPolygonEnd", "ModelPolygonUV", "ModelPolygonVertex", "NewCamera",
        "NewDisplay", "NewLOD", "NewNode", "NewObject3D",
        "NewSEQ", "NewWindow", "NewWorld", "NodeSetActive",
        "NodeSetDescription", "NodeSetCanModify", "NodeSetLighting", "NodeSetOverwrite",
        "Object3DAddChild", "Object3DRegisterTexturesToWorld", "Object3DRotate", "Object3DScale",
        "Object3DSetActionPriority", "Object3DSetActive", "Object3DSetColor", "Object3DSetFacade",
        "Object3DSetOpacityIsSet", "Object3DSetOpacity", "Object3DSetPoints", "Object3DSetPriority",
        "Object3DSetScrollAlways", "Object3DSetScroll", "Object3DSetShowBackFace", "Object3DSetTextureWorldBaseCoordinates",
        "Object3DSetMorphVertex", "Object3DTranslate", "PerspectiveTexture", "PrintNodeCount",
        "PrintUsedNodes", "RdrAddPath", "RdrSetPath", "SEQAddChild",
        "SEQNew", "SEQSetActive", "SEQSetLoop", "SEQSetRepeat",
        "SetAltitudeSurface", "SetBFETolerance", "SetCoplanarTolerance", "SetColinearTolerance",
        "SetGameZNodeArraySize", "SetMaterialArraySize", "SetModel3DArraySize", "SetIntersectBBOX",
        "SetIntersectSurface", "SetLandmark", "SetPaletteName", "SetPaletteShading",
        "SetPerspectiveAdaptiveCorrection", "SetPerspectiveTextureDeltaX", "SetInverseZTolerance", "SetPerspectiveTextureFarZ",
        "SetProximity", "SetTextureDirectory", "SetVertexShading", "TextureAdd",
        "Verbose", "WindowAddClearPolygonVertex", "WindowCloseClearPolygon", "WindowOrigin",
        "WindowResolution", "WindowSetClearPolygon", "WorldAddLight", "WorldExtents",
        "WorldOrigin", "WorldPartitionInclusionTolerance", "WorldPartitionMaxDECFeatureCount", "WorldPartition",
        "WorldSetFogAltitude", "WorldSetFogColor", "WorldSetFogDensity", "WorldSetFogState",
        "WorldSetVirtualPartition", "WriteTextureSetType", "WriteTextureSetMap",
    ];
    internal static string Core(string token)
    {
        foreach (string command in Prefixes)
            if (token.StartsWith(command, StringComparison.Ordinal)) return command;
        return token;
    }
}
