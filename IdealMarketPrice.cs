using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace IdealMarketPriceMod
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class IdealMarketPrice : BaseUnityPlugin
    {
        public const string PluginGuid = "com.clonniq.idealmarketprice";
        public const string PluginName = "Ideal Market Price";
        public const string PluginVersion = "1.1.0";

        // 2.0 = закупочная цена + 100% прибыли.
        private const float SELL_PRICE_MULTIPLIER = 2.0f;
        private const float DAY_START_DELAY = 0.35f;

        internal static IdealMarketPrice Instance;
        internal static ManualLogSource Log;

        private Harmony _harmony;
        private bool _updating;
        private float _lastManualApply = -10f;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            try
            {
                _harmony = new Harmony(PluginGuid);
                _harmony.PatchAll(typeof(IdealMarketPrice));
                Log.LogInfo(string.Format("{0} {1} loaded. Sell price = purchase price x {2:0.##}.", PluginName, PluginVersion, SELL_PRICE_MULTIPLIER));
                Log.LogInfo("F5 = apply prices manually; prices are also updated after RpcStartDay on the host.");
            }
            catch (Exception ex)
            {
                Log.LogError("Initialization failed:");
                Log.LogError(ex);
            }
        }

        private void Update()
        {
            try
            {
                if (!Input.GetKeyDown(KeyCode.F5))
                    return;

                if (Time.unscaledTime - _lastManualApply < 1f)
                    return;

                _lastManualApply = Time.unscaledTime;

                if (!NetworkServer.active)
                {
                    Log.LogWarning("F5: not the server/host, so prices were not changed.");
                    return;
                }

                StartPriceUpdate("F5");
            }
            catch (Exception ex)
            {
                Log.LogError("F5 handler failed:");
                Log.LogError(ex);
            }
        }

        [HarmonyPatch(typeof(GameData), "RpcStartDay")]
        [HarmonyPostfix]
        private static void RpcStartDayPostfix()
        {
            try
            {
                if (Instance == null || !NetworkServer.active)
                    return;

                Instance.StartPriceUpdate("New Day");
            }
            catch (Exception ex)
            {
                if (Instance != null)
                {
                    Instance.Log.LogError("RpcStartDay postfix failed:");
                    Instance.Log.LogError(ex);
                }
            }
        }

        private void StartPriceUpdate(string reason)
        {
            try
            {
                if (_updating)
                {
                    Log.LogWarning("Price update already running; skipped: " + reason);
                    return;
                }

                StartCoroutine(ApplyPricesCoroutine(reason));
            }
            catch (Exception ex)
            {
                Log.LogError("Could not start price update:");
                Log.LogError(ex);
            }
        }

        private IEnumerator ApplyPricesCoroutine(string reason)
        {
            _updating = true;

            try
            {
                if (reason == "New Day")
                    yield return new WaitForSecondsRealtime(DAY_START_DELAY);

                ProductListing listing = null;

                try
                {
                    listing = UnityEngine.Object.FindObjectOfType<ProductListing>();
                }
                catch (Exception ex)
                {
                    Log.LogError("Could not find ProductListing:");
                    Log.LogError(ex);
                    yield break;
                }

                if (listing == null)
                {
                    Log.LogWarning("ProductListing is null; prices were not changed.");
                    yield break;
                }

                FieldInfo productsDataField = typeof(ProductListing).GetField(
                    "productsData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo availableProductsField = typeof(ProductListing).GetField(
                    "availableProducts", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo tierInflationField = typeof(ProductListing).GetField(
                    "tierInflation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (productsDataField == null || availableProductsField == null || tierInflationField == null)
                {
                    Log.LogError("Required ProductListing fields were not found in this game version.");
                    yield break;
                }

                object productsDataObject = productsDataField.GetValue(listing);
                object availableProductsObject = availableProductsField.GetValue(listing);
                object tierInflationObject = tierInflationField.GetValue(listing);

                if (productsDataObject == null || availableProductsObject == null || tierInflationObject == null)
                {
                    Log.LogWarning("One or more ProductListing data objects are null; update skipped.");
                    yield break;
                }

                Array productsData = productsDataObject as Array;
                Array tierInflation = tierInflationObject as Array;
                IEnumerable availableProducts = availableProductsObject as IEnumerable;

                if (productsData == null || tierInflation == null || availableProducts == null)
                {
                    Log.LogError("ProductListing data has an unexpected type.");
                    yield break;
                }

                Type productDataType = typeof(ProductListing).GetNestedType(
                    "ProductData", BindingFlags.Public | BindingFlags.NonPublic);

                if (productDataType == null)
                {
                    Log.LogError("ProductListing.ProductData was not found.");
                    yield break;
                }

                FieldInfo basePriceField = productDataType.GetField(
                    "basePricePerUnit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo productTierField = productDataType.GetField(
                    "productTier", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (basePriceField == null || productTierField == null)
                {
                    Log.LogError("ProductData price/tier fields were not found.");
                    yield break;
                }

                int changed = 0;
                int skipped = 0;

                foreach (object productObject in availableProducts)
                {
                    try
                    {
                        if (productObject == null)
                        {
                            skipped++;
                            continue;
                        }

                        int productId = Convert.ToInt32(productObject);
                        if (productId < 0 || productId >= productsData.Length)
                        {
                            skipped++;
                            continue;
                        }

                        object productData = productsData.GetValue(productId);
                        if (productData == null)
                        {
                            skipped++;
                            continue;
                        }

                        int tier = Convert.ToInt32(productTierField.GetValue(productData));
                        if (tier < 0 || tier >= tierInflation.Length)
                        {
                            skipped++;
                            continue;
                        }

                        float purchasePrice = Convert.ToSingle(basePriceField.GetValue(productData));
                        float inflation = Convert.ToSingle(tierInflation.GetValue(tier));

                        if (float.IsNaN(purchasePrice) || float.IsInfinity(purchasePrice) || purchasePrice < 0f ||
                            float.IsNaN(inflation) || float.IsInfinity(inflation) || inflation <= 0f)
                        {
                            skipped++;
                            continue;
                        }

                        float purchaseMarketPrice = purchasePrice * inflation;
                        float sellPrice = Mathf.Round(purchaseMarketPrice * SELL_PRICE_MULTIPLIER * 100f) / 100f;

                        if (float.IsNaN(sellPrice) || float.IsInfinity(sellPrice) || sellPrice < 0f)
                        {
                            skipped++;
                            continue;
                        }

                        if (listing == null)
                        {
                            Log.LogWarning("ProductListing became null during update; stopping safely.");
                            yield break;
                        }

                        listing.CmdUpdateProductPrice(productId, sellPrice);
                        changed++;

                        if (changed % 10 == 0)
                            yield return null;
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        Log.LogError("Failed to update one product:");
                        Log.LogError(ex);
                    }
                }

                Log.LogInfo(string.Format("Price update finished ({0}). Changed={1}, skipped={2}, multiplier=x{3:0.##}.", reason, changed, skipped, SELL_PRICE_MULTIPLIER));
            }
            catch (Exception ex)
            {
                Log.LogError("Price update failed safely without propagating the exception:");
                Log.LogError(ex);
            }
            finally
            {
                _updating = false;
            }
        }

        private void OnDestroy()
        {
            try
            {
                if (_harmony != null)
                {
                    _harmony.UnpatchSelf();
                    _harmony = null;
                }
            }
            catch (Exception ex)
            {
                if (Log != null)
                    Log.LogError(ex);
            }
        }
    }
}