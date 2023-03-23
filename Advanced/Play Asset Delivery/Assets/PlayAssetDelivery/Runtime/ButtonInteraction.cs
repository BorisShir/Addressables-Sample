using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace AddressablesPlayAssetDelivery
{
    public class ButtonInteraction : MonoBehaviour
    {
        public AssetReference reference;
        public Transform parent;
        public Text text;

        bool isLoading = false;
        GameObject obj = null;
        string baseText = null;

        public void OnButtonClicked()
        {
            if (text.text == "Exit")
            {
                Application.Quit();
                return;
            }
            if (isLoading)
            {
                Debug.LogError("Loading operation currently in progress.");
                return;
            }
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
                text.text = baseText;
            }
        }

        public void OnEnable()
        {
            baseText = text.text;
        }

        IEnumerator Instantiate()
        {
            isLoading = true;
            AsyncOperationHandle<GameObject> handle = Addressables.InstantiateAsync(reference, parent);
            yield return handle;
            obj = handle.Result;
            var texture = obj.GetComponent<Renderer>().material.mainTexture as Texture2D;
            text.text = $"{baseText} {texture.format}";
            isLoading = false;
        }
    }
}
