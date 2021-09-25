using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using KSP.Localization;
using KSP.UI.Screens;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace KSPCommunityFixes
{
    class BetterCargoPartVolume : BasePatch
    {
        private const string CARGO_INFO_NAME = "ModuleCargoPartInfo";

        private static HashSet<AvailablePart> dynamicVolumeCargoParts = new HashSet<AvailablePart>();
        private static Dictionary<Type, MethodInfo> APIModules_CurrentCargoVolume = new Dictionary<Type, MethodInfo>();
        private static Dictionary<Type, MethodInfo> APIModules_UseMultipleVolumes = new Dictionary<Type, MethodInfo>();
        private static Dictionary<AvailablePart, float> partVolumes = new Dictionary<AvailablePart, float>();
        private static Dictionary<AvailablePart, Dictionary<string, float>> partVariantsVolumes = new Dictionary<AvailablePart, Dictionary<string, float>>();
        private static MethodInfo ModuleInventoryPart_UpdateMassVolumeDisplay;
        private static List<string> cargoModulesNames = new List<string>();
        private static ConfigNode volumeCache;
        private static bool volumeCacheIsValid;
        private static Stopwatch loadWatch = new Stopwatch();

        protected override void ApplyPatches(ref List<PatchInfo> patches)
        {
            ModuleInventoryPart_UpdateMassVolumeDisplay = AccessTools.Method(typeof(ModuleInventoryPart), "UpdateMassVolumeDisplay");

            foreach (AssemblyLoader.LoadedAssembly loadedAssembly in AssemblyLoader.loadedAssemblies)
            {
                foreach (Type type in loadedAssembly.assembly.GetTypes())
                {
                    if (typeof(PartModule).IsAssignableFrom(type))
                    {
                        // private/public float CFAPI_CurrentCargoVolume()
                        MethodInfo getCargoVolume = type.GetMethod("CFAPI_CurrentCargoVolume", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                        // private/public bool CFAPI_UseMultipleVolume()
                        MethodInfo useMultipleVolume = type.GetMethod("CFAPI_UseMultipleVolumes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                        if ((getCargoVolume != null && useMultipleVolume == null) || (getCargoVolume == null && useMultipleVolume != null))
                        {
                            Debug.LogError($"[BetterCargoPartVolume] Incorrect cargo volume API implementation for {type.Name} from {loadedAssembly.assembly.GetName().Name}\n" +
                                           "The PartModule must implement both the CFAPI_CurrentCargoVolume and CFAPI_UseMultipleVolumes methods");
                        }
                        else if (getCargoVolume != null && useMultipleVolume != null)
                        {
                            APIModules_CurrentCargoVolume[type] = getCargoVolume;
                            APIModules_UseMultipleVolumes[type] = useMultipleVolume;
                            Debug.Log($"[BetterCargoPartVolume] Cargo volume API implementation registered for module {type.Name} from plugin {loadedAssembly.assembly.GetName().Name}");
                        }

                        // private/public static Func<float, Part> CFAPI_GetPackedVolumeForPart;
                        FieldInfo GetPackedVolumeFunc = type.GetField("CFAPI_GetPackedVolumeForPart", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (GetPackedVolumeFunc != null && GetPackedVolumeFunc.FieldType == typeof(Func<Part, float>))
                        {
                            MethodInfo getPackedVolumeInfo = AccessTools.Method(typeof(BetterCargoPartVolume), nameof(GetPackedVolume));
                            Func<Part, float> getPackedVolumeDelegate = (Func<Part, float>) Delegate.CreateDelegate(typeof(Func<Part, float>), getPackedVolumeInfo);
                            GetPackedVolumeFunc.SetValue(null, getPackedVolumeDelegate);
                            Debug.Log($"[BetterCargoPartVolume] CFAPI_GetPackedVolumeForPart delegate populated for {type.Name} from {loadedAssembly.assembly.GetName().Name}");
                        }
                    }

                    if (typeof(ModuleCargoPart).IsAssignableFrom(type))
                    {
                        cargoModulesNames.Add(type.Name);
                    }
                }
            }

            GameEvents.OnPartLoaderLoaded.Add(SetCargoPartsVolume);

            patches.Add(new PatchInfo(
                PatchMethodType.Prefix,
                AccessTools.Method(typeof(ModuleCargoPart), nameof(ModuleCargoPart.GetInfo)),
                GetType()));

            patches.Add(new PatchInfo(
                PatchMethodType.Prefix,
                AccessTools.Method(typeof(ModuleInventoryPart), "PartDroppedOnInventory"),
                GetType()));

            patches.Add(new PatchInfo(
                PatchMethodType.Prefix,
                AccessTools.Method(typeof(ModuleInventoryPart), "PreviewLimits"),
                GetType()));

            patches.Add(new PatchInfo(
                PatchMethodType.Prefix,
                AccessTools.Method(typeof(ModuleInventoryPart), "HasCapacity"),
                GetType()));

            patches.Add(new PatchInfo(
                PatchMethodType.Prefix,
                AccessTools.Method(typeof(ModuleInventoryPart), "UpdateCapacityValues"),
                GetType()));

            patches.Add(new PatchInfo(
                PatchMethodType.Prefix,
                AccessTools.Method(typeof(UIPartActionInventorySlot), "ProcessClickWithHeldPart"),
                GetType()));

            patches.Add(new PatchInfo(
                PatchMethodType.Postfix,
                AccessTools.Method(typeof(EditorPartIcon), nameof(EditorPartIcon.Create), new Type[]
                {
                    typeof(EditorPartList),
                    typeof(AvailablePart),
                    typeof(StoredPart),
                    typeof(float),
                    typeof(float),
                    typeof(float),
                    typeof(Callback<EditorPartIcon>),
                    typeof(bool),
                    typeof(bool),
                    typeof(PartVariant),
                    typeof(bool),
                    typeof(bool)
                }),
                GetType()));

            // Make ModuleCargoPart.packedVolume persistent
            Lib.EditPartModuleKSPFieldAttributes(
                typeof(ModuleCargoPart),
                nameof(ModuleCargoPart.packedVolume),
                kspField => kspField.isPersistant = true);
        }

        protected override void OnLoadData(ConfigNode node)
        {
            volumeCache = node;
        }

        private static void ParseVolumeCache()
        {
            for (int i = 1; i < volumeCache.values.Count; i++)
            {
                ConfigNode.Value value = volumeCache.values[i];
                AvailablePart ap = PartLoader.getPartInfoByName(value.name.Replace('_', '.'));
                if (ap == null || !float.TryParse(value.value, out float volume))
                {
                    volumeCacheIsValid = false;
                }
                else
                {
                    partVolumes[ap] = volume;
                }
            }

            foreach (ConfigNode node in volumeCache.nodes)
            {
                AvailablePart ap = PartLoader.getPartInfoByName(node.name.Replace('_', '.'));
                if (ap == null)
                {
                    volumeCacheIsValid = false;
                }
                else
                {
                    Dictionary<string, float> variantsVolume = new Dictionary<string, float>();
                    foreach (ConfigNode.Value variantValue in node.values)
                    {
                        if (float.TryParse(variantValue.value, out float volume))
                        {
                            variantsVolume[variantValue.name] = volume;
                        }
                    }

                    partVariantsVolumes[ap] = variantsVolume;
                }
            }
        }

        public void SetCargoPartsVolume()
        {
            loadWatch.Restart();

            dynamicVolumeCargoParts.Clear();
            partVariantsVolumes.Clear();

            string mmSHAPath = Path.Combine(Path.GetFullPath(KSPUtil.ApplicationRootPath), "GameData", "ModuleManager.ConfigSHA");
            ConfigNode mmSHANode = ConfigNode.Load(mmSHAPath);
            string mmSha = null;
            string cacheSHA = null;
            volumeCacheIsValid = mmSHANode != null && mmSHANode.TryGetValue("SHA", ref mmSha) && volumeCache != null && volumeCache.TryGetValue("mmCacheSHA", ref cacheSHA) && mmSha == cacheSHA;
            if (volumeCacheIsValid)
            {
                ParseVolumeCache();
            }

            foreach (AvailablePart availablePart in PartLoader.Instance.loadedParts)
            {
                ModulePartVariants partVariant = availablePart.partPrefab.variants;
                ModuleCargoPart cargoModule = null;

                foreach (PartModule partPrefabModule in availablePart.partPrefab.Modules)
                {
                    if (partPrefabModule is ModuleCargoPart)
                    {
                        cargoModule = (ModuleCargoPart) partPrefabModule;
                    }
                    else if (APIModules_UseMultipleVolumes.TryGetValue(partPrefabModule.GetType(), out MethodInfo UseMultipleVolumes))
                    {
                        bool useMultipleVolumes;
                        try
                        {
                            useMultipleVolumes = (bool) UseMultipleVolumes.Invoke(partPrefabModule, null);
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[BetterCargoPartVolume] Error calling {partPrefabModule.moduleName}.CFAPI_UseMultipleVolumes() on part prefab {availablePart.name}\n{e}");
                            useMultipleVolumes = false;
                        }

                        if (useMultipleVolumes)
                        {
                            dynamicVolumeCargoParts.Add(availablePart);
                        }
                    }
                }

                if (cargoModule == null || cargoModule.packedVolume != 0f)
                {
                    continue;
                }

                if (partVariant != null)
                {
                    Dictionary<string, float> partVariantVolumes;

                    PartVariant baseVariant = partVariant.part.baseVariant;
                    if (baseVariant == null)
                        baseVariant = partVariant.variantList[0];

                    if (!partVariantsVolumes.TryGetValue(availablePart, out partVariantVolumes))
                        partVariantVolumes = new Dictionary<string, float>();

                    List<string> automaticVolumeVariants = new List<string>();
                    bool cacheIsValid = true;
                    foreach (PartVariant variant in partVariant.variantList)
                    {
                        if (partVariantVolumes.ContainsKey(variant.Name))
                            continue;

                        string volumeInfo = variant.GetExtraInfoValue("packedVolume");
                        if (!string.IsNullOrEmpty(volumeInfo) && float.TryParse(volumeInfo, out float variantVolume))
                        {
                            cacheIsValid = false;
                            partVariantVolumes[variant.Name] = variantVolume;
                        }
                        else if (variant.InfoGameObjects.Count > 0 || variant.Name == baseVariant.Name)
                        {
                            cacheIsValid = false;
                            automaticVolumeVariants.Add(variant.Name);
                        }
                    }

                    if (automaticVolumeVariants.Count > 0)
                    {
                        partVariant.gameObject.SetActive(true);
                        try
                        {
                            foreach (string variant in automaticVolumeVariants)
                            {
                                partVariant.SetVariant(variant);
                                float volume = GetPackedVolume(partVariant.part);
                                if (volume <= 0f)
                                {
                                    Debug.LogWarning($"[BetterCargoPartVolume] Unable to find volume for variant {variant} in {partVariant.part.name}");
                                }
                                else
                                {
                                    Debug.Log($"[BetterCargoPartVolume] Automatic volume for variant {variant} in {partVariant.part.name} : {volume:0.0}L");
                                }

                                partVariantVolumes[variant] = volume;
                            }
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"[BetterCargoPartVolume] Unable to find volume for {partVariant.part.name}\n{e}");
                            cargoModule.packedVolume = -1f;
                        }
                        finally
                        {
                            partVariant.gameObject.SetActive(false);
                            partVariant.SetVariant(baseVariant.Name);
                        }
                    }

                    if (partVariantVolumes.Count == 0 || !partVariantVolumes.ContainsKey(baseVariant.Name))
                    {
                        cargoModule.packedVolume = -1f;
                    }
                    else if (partVariantVolumes.Count == 1)
                    {
                        cargoModule.packedVolume = partVariantVolumes[baseVariant.Name];
                        if (!cacheIsValid)
                        {
                            partVariantsVolumes[availablePart] = partVariantVolumes;
                            volumeCacheIsValid = false;
                        }
                    }
                    else
                    {
                        cargoModule.packedVolume = partVariantVolumes[baseVariant.Name];
                        cargoModule.stackableQuantity = 1;
                        dynamicVolumeCargoParts.Add(availablePart);

                        if (!cacheIsValid)
                        {
                            partVariantsVolumes[availablePart] = partVariantVolumes;
                            volumeCacheIsValid = false;
                        }
                    }
                }
                else
                {
                    if (partVolumes.TryGetValue(availablePart, out float volume))
                    {
                        cargoModule.packedVolume = volume;
                    }
                    else
                    {
                        cargoModule.gameObject.SetActive(true);
                        try
                        {
                            volume = GetPackedVolume(cargoModule.part);
                            if (volume <= 0f)
                            {
                                Debug.LogWarning($"[BetterCargoPartVolume] Unable to find volume for {cargoModule.part.name}");
                            }
                            else
                            {
                                Debug.Log($"[BetterCargoPartVolume] Automatic volume for {cargoModule.part.name} : {volume:0.0}L");
                            }

                            cargoModule.packedVolume = volume;
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"[BetterCargoPartVolume] Unable to find volume for {cargoModule.part.name}\n{e}");
                            cargoModule.packedVolume = -1f;
                        }
                        finally
                        {
                            cargoModule.gameObject.SetActive(false);
                        }

                        partVolumes[availablePart] = volume;
                        volumeCacheIsValid = false;
                    }
                }

                // don't update info for ModuleCargoPart derivatives (ModuleGroundPart...)
                if (!cargoModule.GetType().IsSubclassOf(typeof(ModuleCargoPart)))
                {
                    try
                    {
                        UpdateVolumeInfo(availablePart, cargoModule);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[BetterCargoPartVolume] Couldn't update module info for {cargoModule.part.name}\n{e}");
                    }
                }
            }

            int volumeCount = partVolumes.Count + partVariantsVolumes.Count;
            if (volumeCacheIsValid)
            {
                Debug.Log($"[BetterCargoPartVolume] Applied cached cargo volume for {volumeCount} parts");
            }
            else
            {
                Debug.Log($"[BetterCargoPartVolume] Generated cargo volume for {volumeCount} parts");

                if (!string.IsNullOrEmpty(mmSha))
                {
                    volumeCache = new ConfigNode();
                    volumeCache.AddValue("mmCacheSHA", mmSha);

                    foreach (KeyValuePair<AvailablePart, float> cargoPartVolume in partVolumes)
                    {
                        volumeCache.AddValue(cargoPartVolume.Key.name.Replace('.', '_'), cargoPartVolume.Value);
                    }

                    foreach (KeyValuePair<AvailablePart, Dictionary<string, float>> cargoVariantPartVolume in partVariantsVolumes)
                    {
                        ConfigNode apNode = volumeCache.AddNode(cargoVariantPartVolume.Key.name.Replace('.', '_'));
                        foreach (KeyValuePair<string, float> variant in cargoVariantPartVolume.Value)
                        {
                            apNode.AddValue(variant.Key, variant.Value);
                        }
                    }

                    SaveData<BetterCargoPartVolume>(volumeCache);
                    Debug.Log($"[BetterCargoPartVolume] Cargo volume cache has been created");
                }
                else
                {
                    Debug.LogWarning($"[BetterCargoPartVolume] Cargo volume cache couldn't be created as ModuleManager couldn't create the config cache");
                }
            }

            loadWatch.Stop();
            Debug.Log($"[BetterCargoPartVolume] Loading operations took {loadWatch.ElapsedMilliseconds * 0.001:0.000}s");
        }

        private static void UpdateVolumeInfo(AvailablePart availablePart, ModuleCargoPart cargoModule)
        {
            foreach (AvailablePart.ModuleInfo moduleInfo in availablePart.moduleInfos)
            {
                if (moduleInfo.info == CARGO_INFO_NAME)
                {
                    moduleInfo.info = GetCargoModuleInfo(cargoModule);
                }
            }
        }

        private static bool ModuleCargoPart_GetInfo_Prefix(ModuleCargoPart __instance, ref string __result)
        {
            if (__instance.packedVolume == 0f && (HighLogic.LoadedScene == GameScenes.LOADING || PartLoader.Instance.Recompile))
            {
                __result = CARGO_INFO_NAME;
            }
            else
            {
                __result = GetCargoModuleInfo(__instance);
            }

            return false;
        }

        private static string GetCargoModuleInfo(ModuleCargoPart cargoModule)
        {
            StringBuilder sb = StringBuilderCache.Acquire();
            sb.Append(cargoModule.packedVolume > 0f ? Localizer.Format("#autoLOC_8002220") : Localizer.Format("#autoLOC_6002641"));

            if (cargoModule.packedVolume > 0f || cargoModule.stackableQuantity > 1)
            {
                sb.Append("\n\n");

                if (cargoModule.packedVolume > 0f)
                {
                    sb.Append(Localizer.Format("#autoLOC_8003414"));
                    sb.Append(": ");

                    if (cargoModule.part?.partInfo != null && dynamicVolumeCargoParts.Contains(cargoModule.part.partInfo))
                    {
                        sb.Append("variable");
                    }
                    else
                    {
                        sb.Append(cargoModule.packedVolume.ToString("0.0L"));
                    }

                    if (cargoModule.stackableQuantity > 1)
                        sb.Append("\n");
                }

                if (cargoModule.stackableQuantity > 1)
                {
                    sb.Append(Localizer.Format("#autoLOC_8003418"));
                    sb.Append(": ");
                    sb.Append(cargoModule.stackableQuantity.ToString());
                }
            }

            return sb.ToStringAndRelease();
        }

        private static void UpdateDynamicPackedVolume(ModuleCargoPart cargoModule)
        {
            if (cargoModule.part.variants != null && partVariantsVolumes.TryGetValue(cargoModule.part.partInfo, out Dictionary<string, float> variantVolumes))
            {
                if (cargoModule.part.variants.SelectedVariant != null && variantVolumes.TryGetValue(cargoModule.part.variants.SelectedVariant.Name, out float volume))
                {
                    cargoModule.packedVolume = volume;
                    return;
                }

                if (cargoModule.part.baseVariant != null && variantVolumes.TryGetValue(cargoModule.part.baseVariant.Name, out float defaultVolume))
                {
                    cargoModule.packedVolume = defaultVolume;
                    return;
                }
            }

            foreach (PartModule partModule in cargoModule.part.Modules)
            {
                if (APIModules_CurrentCargoVolume.TryGetValue(partModule.GetType(), out MethodInfo getCargoVolume))
                {
                    float volume;
                    try
                    {
                        volume = (float) getCargoVolume.Invoke(partModule, null);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Unable to get packed volume for {cargoModule.part.name} by calling {partModule.moduleName}.GetCargoVolume() :\n{e}");
                        volume = -1f;
                    }

                    cargoModule.packedVolume = volume;
                    break;
                }
            }
        }

        /// <summary>
        /// Get the part bounds volume in liters. For this to work reliably, the part must be world-axis aligned.
        /// </summary>
        public static float GetPackedVolume(Part part)
        {
            float renderersVolume = -1f;
            float collidersVolume = -1f;

            Renderer[] renderers = part.transform.GetComponentsInChildren<Renderer>(false);

            if (renderers.Length > 0)
            {
                Bounds bounds = default;
                bounds.center = part.transform.position;

                foreach (Renderer renderer in renderers)
                {
                    if (!(renderer is MeshRenderer || renderer is SkinnedMeshRenderer))
                        continue;

                    if (renderer.tag != "Untagged" || renderer.gameObject.layer != 0)
                        continue;

                    bounds.Encapsulate(renderer.bounds);
                }

                Vector3 renderersSize = bounds.size;
                renderersVolume = Mathf.Ceil(renderersSize.x * renderersSize.y * renderersSize.z * 1000f);
            }

            Collider[] colliders = part.transform.GetComponentsInChildren<Collider>(false);

            if (colliders.Length > 0)
            {
                Bounds bounds = default;
                bounds.center = part.transform.position;

                foreach (Collider collider in colliders)
                {
                    if (collider.tag != "Untagged" || collider.gameObject.layer != 0)
                        continue;

                    bounds.Encapsulate(collider.bounds);
                }

                Vector3 collidersSize = bounds.size;
                collidersVolume = Mathf.Ceil(collidersSize.x * collidersSize.y * collidersSize.z * 1000f);
            }

            // colliders volume will usually be slightly (5-20%) lower than renderers volume
            // the default choice is the colliders volume, because :
            // - it is more representative of the volume for a "packed" part
            // - the results are more in line with the stock config-defined volumes
            // - renderers volume is less reliable overall, as many modules tend to disable them 
            //   once the part is instantiated (ex : fairing interstages, shrouds...)
            // However, in case the colliders volume is higher than the renderers volume, there 
            // is very likely some rogue colliders messing with us, so the renderers volume
            // is more likely to be accurate.
            if (renderersVolume > 0f && collidersVolume > renderersVolume)
            {
                return renderersVolume;
            }

            if (collidersVolume > 0f)
            {
                return collidersVolume;
            }

            return renderersVolume;
        }

        #region dynamic packedVolume handling

        // Patch everything that read from ModuleCargoPart.packedVolume, and in case the cargo part uses a dynamic volume,
        // force a volume update before processing

        private static void ModuleInventoryPart_PartDroppedOnInventory_Prefix(Part p)
        {
            ModuleCargoPart moduleCargoPart = p.FindModuleImplementing<ModuleCargoPart>();

            if (moduleCargoPart != null && dynamicVolumeCargoParts.Contains(p.partInfo))
            {
                UpdateDynamicPackedVolume(moduleCargoPart);
            }
        }

        private static void ModuleInventoryPart_PreviewLimits_Prefix(Part newPart)
        {
            ModuleCargoPart moduleCargoPart = newPart.FindModuleImplementing<ModuleCargoPart>();

            if (moduleCargoPart != null && dynamicVolumeCargoParts.Contains(newPart.partInfo))
            {
                UpdateDynamicPackedVolume(moduleCargoPart);
            }
        }

        private static void ModuleInventoryPart_HasCapacity_Prefix(Part newPart)
        {
            ModuleCargoPart moduleCargoPart = newPart.FindModuleImplementing<ModuleCargoPart>();

            if (moduleCargoPart != null && dynamicVolumeCargoParts.Contains(newPart.partInfo))
            {
                UpdateDynamicPackedVolume(moduleCargoPart);
            }
        }

        // special case : the stock method use the prefab values for mass/volume, which will fail to be accurate in so many ways that I can't count them.
        // We completely override that method, using the actual stored mass/volume from the protomodule.
        private static bool ModuleInventoryPart_UpdateCapacityValues_Prefix(ModuleInventoryPart __instance, ref float ___volumeOccupied, ref float ___massOccupied)
        {
            ___volumeOccupied = 0f;
            ___massOccupied = 0f;
            for (int i = 0; i < __instance.storedParts.Count; i++)
            {
                StoredPart storedPart = __instance.storedParts.At(i);
                if (storedPart?.snapshot != null)
                {
                    ___massOccupied += storedPart.snapshot.mass * storedPart.quantity;

                    foreach (ProtoPartResourceSnapshot protoPartResourceSnapshot in storedPart.snapshot.resources)
                    {
                        if (protoPartResourceSnapshot?.definition != null)
                        {
                            ___massOccupied += (float) (protoPartResourceSnapshot.amount * protoPartResourceSnapshot.definition.density) * storedPart.quantity;
                        }
                    }

                    foreach (ProtoPartModuleSnapshot protoModule in storedPart.snapshot.modules)
                    {
                        float cargoVolume = 0f;
                        if (cargoModulesNames.Contains(protoModule.moduleName) && protoModule.moduleValues.TryGetValue(nameof(ModuleCargoPart.packedVolume), ref cargoVolume))
                        {
                            ___volumeOccupied += cargoVolume * storedPart.quantity;
                            break;
                        }
                    }
                }
            }

            ModuleInventoryPart_UpdateMassVolumeDisplay.Invoke(__instance, new object[] {true, false});
            return false;
        }

        private static void UIPartActionInventorySlot_ProcessClickWithHeldPart_Prefix()
        {
            foreach (Part part in UIPartActionControllerInventory.Instance.CurrentCargoPart.GetComponentsInChildren<Part>())
            {
                ModuleCargoPart moduleCargoPart = part.FindModuleImplementing<ModuleCargoPart>();

                if (moduleCargoPart != null && dynamicVolumeCargoParts.Contains(part.partInfo))
                {
                    UpdateDynamicPackedVolume(moduleCargoPart);
                }
            }
        }

        private static void EditorPartIcon_Create_Postfix(EditorPartIcon __instance)
        {
            if (__instance.btnSwapTexture != null && __instance.inInventory && __instance.AvailPart != null && dynamicVolumeCargoParts.Contains(__instance.AvailPart))
            {
                __instance.btnSwapTexture.gameObject.SetActive(false);
            }
        }

        #endregion
    }

    public class BetterCargoPartVolumeTestModule : PartModule
    {
        // That field will be populated by KSPCommunityFixes, if its BetterCargoPartVolume patch is enabled
        private static Func<Part, float> CFAPI_GetPackedVolumeForPart;

        public override void OnLoad(ConfigNode node)
        {
            // Execute only during prefab (re)compilation
            if (CFAPI_GetPackedVolumeForPart != null && (HighLogic.LoadedScene == GameScenes.LOADING || PartLoader.Instance.Recompile))
            {
                Debug.Log($"[TEST] volume for {part.name} is {CFAPI_GetPackedVolumeForPart(part):0}L");
            }
        }
        // return a volume in liters, or -1 if you want to prevent
        // the part from being storable in inventories (it will still
        // be manipulable in EVA construction mode)
        private float CFAPI_CurrentCargoVolume()
        {
            return -1f;
        }

        private bool CFAPI_UseMultipleVolumes()
        {
            return true;
        }
    }
}