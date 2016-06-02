﻿using DecisionServicePrivateWeb.Classes;
using DecisionServicePrivateWeb.Models;
using Microsoft.ApplicationInsights;
using Microsoft.Research.MultiWorldTesting.Contract;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Mvc;
using ApplicationMetadataStore;

namespace DecisionServicePrivateWeb.Controllers
{
    public class HomeController : Controller
    {
        const string SKAuthenticated = "Authenticated";

        const string SKClientSettingsBlob = "ClientSettingsBlob";
        const string SKExtraSettingsBlob = "ExtraSettingsBlob";
        const string SKEvalContainer = "EvalContainer";

        const string SKClientSettings = "ClientSettings";
        const string SKExtraSettings = "ExtraSettings";

        public ActionResult Index()
        {
            return View(new IndexViewModel { Authenticated = IsAuthenticated(Session) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Index(string password)
        {
            string correctPassword = ConfigurationManager.AppSettings[ApplicationMetadataStore.ApplicationMetadataStore.AKPassword];
            if (string.Equals(password, correctPassword))
            {
                Session[SKAuthenticated] = true;

                // Create again in case the settings were not created at start up
                ApplicationMetadataStore.ApplicationMetadataStore.CreateSettingsBlobIfNotExists();

                string azureStorageConnectionString = ConfigurationManager.AppSettings[ApplicationMetadataStore.ApplicationMetadataStore.AKConnectionString];
                var storageAccount = CloudStorageAccount.Parse(azureStorageConnectionString);
                var blobClient = storageAccount.CreateCloudBlobClient();
                var settingsBlobContainer = blobClient.GetContainerReference(ApplicationBlobConstants.SettingsContainerName);

                Session[SKClientSettingsBlob] = settingsBlobContainer.GetBlockBlobReference(ApplicationBlobConstants.LatestClientSettingsBlobName);
                Session[SKExtraSettingsBlob] = settingsBlobContainer.GetBlockBlobReference(ApplicationBlobConstants.LatestExtraSettingsBlobName);
                Session[SKEvalContainer] = blobClient.GetContainerReference(ApplicationBlobConstants.OfflineEvalContainerName);

                return Redirect(Url.Action("Settings"));
            }
            return View(new IndexViewModel { Authenticated = false, Error = "Invalid Password" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult LogOff()
        {
            Session.Clear();
            return RedirectToAction("Index");
        }

        [AllowAnonymous]
        public ActionResult Settings()
        {
            if (!IsAuthenticated(Session))
            {
                return RedirectToAction("Index");
            }
            string userName = User.Identity.Name;
            ApplicationClientMetadata clientApp = null;
            ApplicationExtraMetadata extraApp = null;
            try
            {
                var clientSettingsBlob = (CloudBlockBlob)Session[SKClientSettingsBlob];
                clientApp = JsonConvert.DeserializeObject<ApplicationClientMetadata>(clientSettingsBlob.DownloadText());
                Session[SKClientSettings] = clientApp;

                var extraSettingsBlob = (CloudBlockBlob)Session[SKExtraSettingsBlob];
                extraApp = JsonConvert.DeserializeObject<ApplicationExtraMetadata>(extraSettingsBlob.DownloadText());
                Session[SKExtraSettings] = extraApp;
            }
            catch (Exception ex)
            {
                return new HttpStatusCodeResult(HttpStatusCode.InternalServerError, $"Unable to load application metadata: {ex.ToString()}");
            }

            return View(CreateAppView(clientApp, extraApp));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Settings(SettingsSaveModel model)
        {
            try
            {
                var clientMeta = (ApplicationClientMetadata)Session[SKClientSettings];
                clientMeta.IsExplorationEnabled = model.IsExplorationEnabled;
                clientMeta.TrainArguments = model.TrainArguments;
                var clientSettingsBlob = (CloudBlockBlob)Session[SKClientSettingsBlob];
                clientSettingsBlob.UploadText(JsonConvert.SerializeObject(clientMeta));

                var extraMeta = (ApplicationExtraMetadata)Session[SKExtraSettings];
                extraMeta.ModelId = model.SelectedModelId;
                var extraSettingsBlob = (CloudBlockBlob)Session[SKExtraSettingsBlob];
                extraSettingsBlob.UploadText(JsonConvert.SerializeObject(extraMeta));

                try
                {
                    // copy selected model file to the latest file
                    ApplicationMetadataStore.ApplicationMetadataStore.UpdateModel(model.SelectedModelId, ConfigurationManager.AppSettings[ApplicationMetadataStore.ApplicationMetadataStore.AKConnectionString]);
                }
                catch (Exception ex)
                {
                    return new HttpStatusCodeResult(HttpStatusCode.InternalServerError, $"Unable to update model: {ex.ToString()}");
                }

                return View(CreateAppView(clientMeta, extraMeta));
            }
            catch (Exception ex)
            {
                return new HttpStatusCodeResult(HttpStatusCode.InternalServerError, $"Unable to update application metadata: {ex.ToString()}");
            }
        }


        [AllowAnonymous]
        public ActionResult Evaluation()
        {
            if (!IsAuthenticated(Session))
            {
                return RedirectToAction("Index");
            }

            return View(new EvaluationViewModel { WindowFilters = new List<string>(new string[] { "5m", "20m", "1h", "3h", "6h" }), SelectedFilter = "5m" });
        }

        [AllowAnonymous]
        public ActionResult EvalJson(string windowType = "3h", int maxNumPolicies = 5)
        {
            var policyRegex = "Policy (.*)";
            var regex = new Regex(policyRegex);

            if (!IsAuthenticated(Session))
            {
                return new HttpStatusCodeResult(HttpStatusCode.Unauthorized);
            }

            try
            {
                var evalContainer = (CloudBlobContainer)Session[SKEvalContainer];
                var evalBlobs = evalContainer.ListBlobs(useFlatBlobListing: true);
                var evalData = new Dictionary<string, EvalD3>();
                foreach (var evalBlob in evalBlobs)
                {
                    // TODO: cache or optimize perf
                    var evalBlockBlob = (CloudBlockBlob)evalBlob;
                    if (evalBlockBlob != null)
                    {
                        var evalTextData = evalBlockBlob.DownloadText();
                        var evalLines = evalTextData.Split(new string[] { "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var l in evalLines)
                        {
                            var evalResult = JsonConvert.DeserializeObject<EvalResult>(l);
                            if (evalResult.WindowType != windowType)
                            {
                                continue;
                            }
                            if (Convert.ToInt32(regex.Match(evalResult.PolicyName).Groups[1].Value) > maxNumPolicies)
                            {
                                continue;
                            }

                            if (evalData.ContainsKey(evalResult.PolicyName))
                            {
                                var timeToCost = evalData[evalResult.PolicyName].values;
                                    //.Add(new object[] { evalResult.LastWindowTime, evalResult.AverageCost });

                                if (timeToCost.ContainsKey(evalResult.LastWindowTime))
                                {
                                    timeToCost[evalResult.LastWindowTime] = evalResult.AverageCost;
                                }
                                else
                                {
                                    timeToCost.Add(evalResult.LastWindowTime, evalResult.AverageCost);
                                }
                            }
                            else
                            {
                                evalData.Add(evalResult.PolicyName, new EvalD3 { key = evalResult.PolicyName, values = new Dictionary<DateTime, float>() });
                            }
                        }
                    }
                }

                return Json(evalData.Values.Select(a => new { key = a.key, values = a.values.Select(v => new object[] { v.Key, v.Value }) }), JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return new HttpStatusCodeResult(HttpStatusCode.InternalServerError, $"Unable to load evaluation result: {ex.ToString()}");
            }
        }

        public static string GetDecisionTypeString(DecisionType decisionType)
        {
            switch (decisionType)
            {
                case DecisionType.SingleAction:
                    return "Features";
                case DecisionType.MultiActions:
                    return "Action Dependent Features";
                default:
                    return string.Empty;
            }
        }

        public static bool IsAuthenticated(HttpSessionStateBase Session)
        {
            return (Session[SKAuthenticated] != null && (bool)Session[SKAuthenticated]);
        }


        private static SettingsViewModel CreateAppView(
            ApplicationClientMetadata clientMetadata,
            ApplicationExtraMetadata extraMetadata)
        {
            var svm = new SettingsViewModel
            {
                ApplicationId = clientMetadata.ApplicationID,
                AzureSubscriptionId = extraMetadata.SubscriptionId,
                DecisionType = extraMetadata.DecisionType,
                NumActions = clientMetadata.NumActions,
                TrainFrequency = extraMetadata.TrainFrequency,
                TrainArguments = clientMetadata.TrainArguments,
                AzureStorageConnectionString = ConfigurationManager.AppSettings[ApplicationMetadataStore.ApplicationMetadataStore.AKConnectionString],
                AzureResourceGroupName = extraMetadata.AzureResourceGroupName,
                ApplicationInsightsName = extraMetadata.AzureResourceGroupName + "-appinsights",
                OnlineTrainerName = extraMetadata.AzureResourceGroupName + "-trainer",
                WebApiName = extraMetadata.AzureResourceGroupName + "-webapi",
                WebManageName = extraMetadata.AzureResourceGroupName + "-mc",
                ASAEvalName = extraMetadata.AzureResourceGroupName + "-eval",
                ASAJoinName = extraMetadata.AzureResourceGroupName + "-join",
                EventHubInteractionConnectionString = clientMetadata.EventHubInteractionConnectionString,
                EventHubObservationConnectionString = clientMetadata.EventHubObservationConnectionString,
                ExperimentalUnitDuration = extraMetadata.ExperimentalUnitDuration,
                ModelIdList = new List<BlobModelViewModel>(),
                SelectedModelId = extraMetadata.ModelId,
                IsExplorationEnabled = clientMetadata.IsExplorationEnabled
            };
            svm.ModelIdList.Add(new BlobModelViewModel { Name = "Latest" });
            return svm;
        }

    }
}