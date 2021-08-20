using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace KSPCommunityFixes
{
    class AutomaticCargoPartVolume : BasePatch
    {
        protected override void ApplyPatches(ref List<PatchInfo> patches)
        {
            patches.Add(new PatchInfo(
                PatchMethodType.Prefix,
                AccessTools.Method(typeof(ModuleCargoPart), nameof(ModuleCargoPart.OnLoad)),
                GetType()));
        }

        static void ModuleCargoPart_OnLoad_Prefix(ModuleCargoPart __instance, ConfigNode node)
        {
            if (__instance.packedVolume != 0f)
                return;

            Collider[] colliders = __instance.part.transform.GetComponentsInChildren<Collider>(false);

            if (colliders.Length > 0)
            {
                Bounds bounds = colliders[0].bounds;

                for (int i = 1; i < colliders.Length; i++)
                {
                    bounds.Encapsulate(colliders[i].bounds);
                }

                __instance.packedVolume = Mathf.Ceil(bounds.size.x * bounds.size.y * bounds.size.z * 1000f);
                Debug.Log($"[AutomaticCargoPartVolume] Volume for {__instance.part.name} : {__instance.packedVolume:F0}L");
            }

            if (__instance.packedVolume == 0f)
            {
                Debug.LogWarning($"[AutomaticCargoPartVolume] Unable to find volume for {__instance.part.name}");
                __instance.packedVolume = -1f;
            }
        }
    }
}