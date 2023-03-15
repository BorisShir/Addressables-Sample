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
            var obj1 = GameObject.Find("Cylinder");
            var texture1 = obj1.GetComponent<Renderer>().material.mainTexture as Texture2D;
            UnityEngine.Debug.Log($"Cylinder texture compression {texture1.format}");

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
                    var texture = obj.GetComponent<Renderer>().material.mainTexture as Texture2D;
                    UnityEngine.Debug.Log($"Object texture compression {texture.format}");

                    // Unload the object
                    Addressables.ReleaseInstance(obj);
                    obj = null;
                }
            }
        }

        IEnumerator Instantiate()
        {
            isLoading = true;
            AsyncOperationHandle<GameObject> handle = Addressables.InstantiateAsync(reference, parent);
            yield return handle;
            obj = handle.Result;
            isLoading = false;
        }
    }
}
