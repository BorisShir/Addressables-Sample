using System.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace AddressablesPlayAssetDelivery
{
    public class ButtonInteraction : MonoBehaviour
    {
        public AssetReference reference;
        public Transform parent;

        bool isLoading = false;
        GameObject obj = null;

        public void OnButtonClicked()
        {
            if (isLoading)
                Debug.LogError("Loading operation currently in progress.");
            else if (!isLoading)
            {
                if (obj == null)
                {
                    // Load the object
                    StartCoroutine(Instantiate());
                }
                else
                {
                    // Unload the object
                    Addressables.ReleaseInstance(obj);
                    obj = null;
                }
            }
        }

        IEnumerator Instantiate()
        {
            isLoading = true;

            // Force addressing asset bundle for the second time
            // Required only to demonstrate problem when inheriting PlayAssetDeliveryAssetBundleProvider from AssetBundleProvider 
            var handleDep = Addressables.DownloadDependenciesAsync(reference);
            yield return handleDep;

            var handle = Addressables.InstantiateAsync(reference, parent);
            yield return handle;
            obj = handle.Result;
            isLoading = false;
        }
    }
}
