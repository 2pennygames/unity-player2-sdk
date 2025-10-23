#pragma warning disable CS0618

using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
#if UNITY_EDITOR
using UnityEditor;
#endif

public enum ChatState
{
    INACTIVE,
    BEGIN,
    INTERACTING,
    PROCESSING
}

namespace player2_sdk
{
    [Serializable]
    public class Function
    {
        [Tooltip(
            "The name of the function, used by the LLM to call this function, so try to keep it short and to the point")]
        public string name;

        [Tooltip("A short description of the function, used for explaining to the LLM what this function does")]
        public string description;

        public List<FunctionArgument> functionArguments;

        [Tooltip("If true, this function will never respond with a message when called")]
        public bool neverRespondWithMessage;

        public Function(string name, string description, List<FunctionArgument> functionArguments, bool neverRespondWithMessage)
        {
            this.name = name;
            this.description = description;
            this.functionArguments = functionArguments;
            this.neverRespondWithMessage = neverRespondWithMessage;
        }

        public SerializableFunction ToSerializableFunction()
        {
            var props = new Dictionary<string, SerializedArguments>();

            for (var i = 0; i < functionArguments.Count; i++)
            {
                var arg = functionArguments[i];
                props[arg.argumentName] = new SerializedArguments
                {
                    type = arg.argumentType,
                    description = arg.argumentDescription
                };
            }

            Debug.Log(props);
            return new SerializableFunction
            {
                name = name,
                description = description,
                parameters = new Parameters
                {
                    Properties = props,
                    required = functionArguments.FindAll(arg => arg.required).ConvertAll(arg => arg.argumentName)
                },
                neverRespondWithMessage = neverRespondWithMessage
            };
        }
    }


    [Serializable]
    public class FunctionArgument
    {
        public string argumentName;
        public string argumentType;
        public string argumentDescription;
        public bool required;

        public FunctionArgument(string argumentName, string argumentType, string argumentDescription, bool required)
        {
            this.argumentName = argumentName;
            this.argumentType = argumentType;
            this.argumentDescription = argumentDescription;
            this.required = required;
        }
    }


    public class NpcManager : MonoBehaviour
    {
        private ChatState _chatState = ChatState.INACTIVE;
        internal ChatState CurrentChatState {
            get
            {
                return _chatState;
            }

            set
            {
                switch (value)
                {
                    case ChatState.INACTIVE:
                        Debug.Log($"NpcManager:: ChatState set to INACTIVE. Enabling player interaction");

                        player.AllowInteraction();
                        UIManager.Instance.DisableTrustSlider();

                        break;

                    case ChatState.BEGIN:
                        Debug.Log($"NpcManager:: ChatState set to BEGIN. Enabling inputField and disabling interaction");

                        inputField.interactable = true;
                        player.DisallowInteraction();
                        player.AllowMovement();

                        UIManager.Instance.EnableTrustSlider();

                        break;

                    case ChatState.INTERACTING:
                        Debug.Log($"NpcManager:: ChatState set to INTERACTING. Disabling player movement");

                        player.DisallowMovement();

                        break;

                    case ChatState.PROCESSING:
                        Debug.Log($"NpcManager:: ChatState set to PROCESSING. Disabling inputField and enabling player movement");

                        inputField.interactable = false;
                        player.AllowMovement();

                        break;
                }

                _chatState = value;
            }
        }

        [SerializeField] private Player player;

        private const string BaseUrl = "https://api.player2.game/v1";

        [Header("Config")]
        [SerializeField]
        [Tooltip(
            "The Client ID is used to identify your game. It can be acquired from the Player2 Developer Dashboard")]
        public string clientId;

        [SerializeField]
        [Tooltip(
            "If true, the NPCs will use Text-to-Speech (TTS) to speak their responses. Requires a valid voice_id in the tts.voice_ids configuration.")]
        public bool TTS;

        [SerializeField]
        [Tooltip("If true, the NPCs will keep track of game state information in the conversation history.")]
        public bool keepGameState;

        [Header("TMP")]
        [SerializeField] internal TMP_InputField inputField;
        [SerializeField] private TextMeshProUGUI outputField;

        public readonly JsonSerializerSettings JsonSerializerSettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new SnakeCaseNamingStrategy()
            }
        };

        private Player2NpcResponseListener _responseListener;

        [NonSerialized] public UnityEvent apiTokenReady = new();

        [NonSerialized] public UnityEvent<string> NewApiKey = new();

        [NonSerialized] public UnityEvent spawnNpcs = new();

        public string ApiKey { get; private set; }

        internal void SetChatState(ChatState chatState)
        {
            this.CurrentChatState = chatState;
        }

        private void Awake()
        {
            Debug.Log("=== NpcManager.Awake: Starting initialization ===");
            Debug.Log($"NpcManager.Awake: Platform: {Application.platform}");

#if UNITY_EDITOR
            Debug.Log("NpcManager.Awake: Running in Unity Editor");
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
            Debug.Log("NpcManager.Awake: Running in WebGL build (not editor)");
            // For WebGL builds, we'll handle certificate validation differently
            // This is set at runtime, not in PlayerSettings
#endif

            // Log domain detection status early
            var isOnPlayer2Game = IsWebGLAndOnPlayer2GameDomain();
            Debug.Log($"NpcManager.Awake: On player2.game domain: {isOnPlayer2Game}");
            Debug.Log($"NpcManager.Awake: Base URL will be: {GetBaseUrl()}");
            Debug.Log($"NpcManager.Awake: Will skip authentication: {ShouldSkipAuthentication()}");

            if (string.IsNullOrEmpty(clientId))
            {
                Debug.LogError("NpcManager requires a Client ID to be set.", this);
                return;
            }

            _responseListener = gameObject.GetComponent<Player2NpcResponseListener>();
            if (_responseListener == null)
            {
                Debug.LogError(
                    "Player2NpcResponseListener component not found on NPC Manager GameObject. Please attach it in the editor.",
                    this);
                return;
            }

            _responseListener.JsonSerializerSettings = JsonSerializerSettings;
            _responseListener._baseUrl = GetBaseUrl();

            _responseListener.SetReconnectionSettings(5, 2.5f);

            NewApiKey.AddListener(async apiKey =>
            {
                Debug.Log("NpcManager.NewApiKey listener: Received API key");
                ApiKey = apiKey;
                Debug.Log("NpcManager.NewApiKey listener: API key set");

                // For WebGL on player2.game domain, pass empty API key to skip auth headers
                var skipAuth = ShouldSkipAuthentication();
                var apiKeyForListener = skipAuth ? "" : apiKey;
                Debug.Log($"NpcManager.NewApiKey listener: Skip authentication: {skipAuth}");
                Debug.Log($"NpcManager.NewApiKey listener: Base URL: {GetBaseUrl()}");
                Debug.Log(
                    $"NpcManager.NewApiKey listener: Passing to response listener: {(string.IsNullOrEmpty(apiKeyForListener) ? "empty (skipping auth)" : "API key")}");

                // Set the API key on the response listener
                _responseListener.newApiKey.Invoke(apiKeyForListener);

                // Wait for the response listener to actually be connected before signaling ready
                await WaitForResponseListenerReady();

                // Skip health check if authentication was bypassed (hosted scenario)
                if (skipAuth && string.IsNullOrEmpty(apiKey))
                {
                    Debug.Log(
                        "NpcManager.NewApiKey listener: Authentication bypassed for hosted scenario, skipping health check");
                    apiTokenReady.Invoke();
                }
                else
                {
                    // Verify token works with health check before signaling ready
                    Debug.Log("NpcManager.NewApiKey listener: Response listener connected, performing health check...");
                    var healthCheckPassed = await TokenValidator.ValidateTokenAsync(apiKey, this);

                    if (healthCheckPassed)
                    {
                        Debug.Log("NpcManager.NewApiKey listener: Health check passed, signaling API token ready");
                        apiTokenReady.Invoke();
                    }
                    else
                    {
                        Debug.LogError(
                            "NpcManager.NewApiKey listener: Health check failed, token is not working properly. Not signaling ready.");
                    }
                }
            });

            // Listen for when the authentication system signals it's fully ready
            apiTokenReady.AddListener(() =>
            {
                Debug.Log("NpcManager.apiTokenReady listener: Authentication fully complete, spawning NPCs");
                spawnNpcs.Invoke();
                Debug.Log("NpcManager.apiTokenReady listener: spawnNpcs invoked");
            });

            Debug.Log($"NpcManager initialized with clientId: {clientId}");

            // Automatically start authentication if not already started
            if (!GameManager.debugging)
            {
                StartCoroutine(AutoStartAuthentication());
            }

            inputField.onSelect.AddListener(InputFieldSelectHandler);
            inputField.onDeselect.AddListener(InputFieldDeselectHandler);
            inputField.onSubmit.AddListener(InputFieldSubmitHandler);
        }

        private void InputFieldSelectHandler(string text)
        {
            SetChatState(ChatState.INTERACTING);
        }

        private void InputFieldDeselectHandler(string text)
        {
            Debug.Log($"NpcManager:: InputFieldDeselectHandler, CurrentChatState: {CurrentChatState}");
            if (CurrentChatState == ChatState.INTERACTING)
            {
                // SetChatState(ChatState.BEGIN);
            }
        }

        private void InputFieldSubmitHandler(string message)
        {
            if (UIManager.Instance.currentInteractingSummonable)
            {
                SetChatState(ChatState.PROCESSING);

                StartCoroutine(BeginThinkingTextAnimation());

                UIManager.Instance.currentInteractingSummonable.player2Npc.SendMessageToNPC(message);
            } else
            {
                Debug.LogError("NpcManager:: OnSubmit called when there is no interacting summonable");
            }
        }

        private IEnumerator BeginThinkingTextAnimation()
        {
            const string baseText = "Thinking";
            const int dotLength = 3;
            const float thinkingAnimtationDuration = 0.5f;

            int dotCount = 0;

            while (CurrentChatState == ChatState.PROCESSING)
            {
                outputField.text = baseText + new string('.', dotCount);
                dotCount = (dotCount + 1) % (dotLength + 1);
                yield return new WaitForSeconds(thinkingAnimtationDuration);
            }
        }

        private void OnDestroy()
        {
            if (_responseListener != null) _responseListener.StopListening();
        }


        private void OnValidate()
        {
            if (string.IsNullOrEmpty(clientId))
            {
                Debug.LogError("NpcManager requires a Game ID to be set.", this);
                
            }
        }

        public string GetBaseUrl()
        {
            Debug.Log($"NpcManager.GetBaseUrl: Using standard API URL: {BaseUrl}");
            return BaseUrl;
        }

        /// <summary>
        ///     Check if authentication should be skipped (WebGL on player2.game domain)
        /// </summary>
        public bool ShouldSkipAuthentication()
        {
            var shouldSkip = IsWebGLAndOnPlayer2GameDomain();
            Debug.Log($"NpcManager.ShouldSkipAuthentication: {shouldSkip}");
            return shouldSkip;
        }

        /// <summary>
        ///     Check if we're running in WebGL and on player2.game domain
        /// </summary>
        private bool IsWebGLAndOnPlayer2GameDomain()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            Debug.Log("IsWebGLAndOnPlayer2GameDomain: Running in WebGL build (not editor)");
            try
            {
                // Use Unity's built-in Application.absoluteURL for reliable URL detection
                string absoluteUrl = Application.absoluteURL;
                Debug.Log($"IsWebGLAndOnPlayer2GameDomain: Retrieved absolute URL: '{absoluteUrl}'");
                
                if (string.IsNullOrEmpty(absoluteUrl))
                {
                    Debug.LogWarning("IsWebGLAndOnPlayer2GameDomain: Application.absoluteURL is null or empty");
                    return false;
                }
                
                // Parse the URL to get the host
                System.Uri uri = new System.Uri(absoluteUrl);
                string host = uri.Host;
                Debug.Log($"IsWebGLAndOnPlayer2GameDomain: Parsed host: '{host}'");
                
                bool isPlayer2Game = host.Equals("player2.game", StringComparison.OrdinalIgnoreCase) || 
                                     host.EndsWith(".player2.game", StringComparison.OrdinalIgnoreCase);
                Debug.Log($"IsWebGLAndOnPlayer2GameDomain: Is legitimate player2.game domain: {isPlayer2Game}");
                Debug.Log($"IsWebGLAndOnPlayer2GameDomain: Final result: {isPlayer2Game}");
                
                return isPlayer2Game;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"IsWebGLAndOnPlayer2GameDomain: Failed to detect WebGL domain: {ex.Message}");
                Debug.LogWarning($"IsWebGLAndOnPlayer2GameDomain: Stack trace: {ex.StackTrace}");
                return false;
            }
#else
            Debug.Log(
                "IsWebGLAndOnPlayer2GameDomain: Not running in WebGL build (editor or other platform), returning false");
            return false;
#endif
        }

        private async Awaitable WaitForResponseListenerReady()
        {
            if (_responseListener == null) return;

            // Wait for the response listener to actually establish its connection
            var attempts = 0;
            const int maxAttempts = 50; // 5 seconds max (50 * 100ms)

            while (!_responseListener.IsListening && attempts < maxAttempts)
            {
                await Awaitable.WaitForSecondsAsync(0.1f);
                attempts++;
            }

            if (!_responseListener.IsListening)
                Debug.LogWarning("Response listener failed to connect within timeout, proceeding anyway");
            else
                Debug.Log($"Response listener connected after {attempts * 100}ms");
        }

        private IEnumerator AutoStartAuthentication()
        {
            // Wait a few frames for other components to initialize
            yield return new WaitForSecondsRealtime(0.1f);

            // Check if AuthenticationUI already exists
            var existingAuth = FindObjectOfType<AuthenticationUI>();
            if (existingAuth != null)
            {
                Debug.Log("NpcManager.AutoStartAuthentication: AuthenticationUI already exists, not auto-creating");
                yield break;
            }

            // Auto-setup authentication
            Debug.Log("NpcManager.AutoStartAuthentication: No AuthenticationUI found, auto-creating one");
            AuthenticationUI.Setup(this);
        }


        public void RegisterNpc(string id, GameObject npcObject)
        {
            if (_responseListener == null)
            {
                Debug.LogError("Response listener is null! Cannot register NPC.");
                return;
            }

            if (string.IsNullOrEmpty(id))
            {
                Debug.LogError("Cannot register NPC with empty ID");
                return;
            }

            var uiAttached = outputField != null;
            if (!uiAttached)
                Debug.LogWarning(
                    $"Registering NPC {id} without a TextMeshProUGUI target; responses will not display in UI.");

            Debug.Log($"Registering NPC with ID: {id}");

            var onNpcApiResponse = new UnityEvent<NpcApiChatResponse>();
            onNpcApiResponse.AddListener(response =>
                HandleNpcApiResponse(id, response, uiAttached, outputField, npcObject));

            _responseListener.RegisterNpc(id, onNpcApiResponse);

            // Ensure listener is running after registering
            if (!_responseListener.IsListening)
            {
                Debug.Log("Listener was not running, starting it now");
                _responseListener.StartListening();
            }
        }

        private void HandleNpcApiResponse(string id, NpcApiChatResponse response, bool uiAttached,
            TextMeshProUGUI outputField, GameObject npcObject)
        {
            try
            {
                if (response == null)
                {
                    Debug.LogWarning($"Received null response object for NPC {id}");
                    return;
                }

                if (npcObject == null)
                {
                    Debug.LogWarning($"NPC object is null for NPC {id}");
                    return;
                }

                if (!string.IsNullOrEmpty(response.message))
                {
                    if (uiAttached && outputField != null)
                    {
                        Debug.Log($"Updating UI for NPC {id}: {response.message}");
                        outputField.text = response.message;

                        SetChatState(ChatState.BEGIN);
                    }
                    else
                    {
                        Debug.Log($"(No UI) NPC {id} message: {response.message}");
                    }
                }

                // Handle audio playback if audio data is available
                if (response.audio != null && !string.IsNullOrEmpty(response.audio.data))
                {
                    // Log detailed audio data information for troubleshooting
                    var audioDataPreview = response.audio.data.Length > 100
                        ? response.audio.data.Substring(0, 100) + "..."
                        : response.audio.data;
                    Debug.Log(
                        $"NPC {id} - Audio data received: Length={response.audio.data.Length}, Preview={audioDataPreview}");

                    // Validate audio data format
                    if (response.audio.data.StartsWith("data:"))
                    {
                        var commaIndex = response.audio.data.IndexOf(',');
                        if (commaIndex > 0)
                        {
                            var mimeType = response.audio.data.Substring(0, commaIndex);
                            var base64Data = response.audio.data.Substring(commaIndex + 1);
                            Debug.Log($"NPC {id} - Audio format: {mimeType}, Base64 length: {base64Data.Length}");
                        }
                        else
                        {
                            Debug.LogWarning($"NPC {id} - Invalid data URL format: no comma separator found");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"NPC {id} - Audio data does not start with 'data:' prefix");
                    }

                    // Check if NPC GameObject has AudioSource, add if needed
                    var audioSource = npcObject.GetComponent<AudioSource>();
                    if (audioSource == null) audioSource = npcObject.AddComponent<AudioSource>();

                    // Start coroutine to decode and play audio using platform-specific implementation
                    var audioPlayer = AudioPlayerFactory.GetAudioPlayer();
                    StartCoroutine(audioPlayer.PlayAudioFromDataUrl(response.audio.data, audioSource, id));
                }

                if (response.command == null || response.command.Count == 0) return;

                foreach (var functionCall in response.command)
                    try
                    {
                        var call = functionCall.ToFunctionCall(npcObject);
                        UnityEvent<FunctionCall> functionHandler = npcObject.GetComponent<Player2Npc>().functionHandler;
                        functionHandler?.Invoke(call);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError(
                            $"Error invoking function call '{functionCall?.name}' for NPC {id}: {ex.Message}");
                    }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Unhandled exception processing response for NPC {id}: {ex.Message}");
            }
        }


        public void UnregisterNpc(string id)
        {
            if (_responseListener != null) _responseListener.UnregisterNpc(id);
        }

        public bool IsListenerActive()
        {
            return _responseListener != null && _responseListener.IsListening;
        }

        public void StartListener()
        {
            if (_responseListener != null) _responseListener.StartListening();
        }

        public void StopListener()
        {
            if (_responseListener != null) _responseListener.StopListening();
        }

        // Add this method for debugging
        [ContextMenu("Debug Listener Status")]
        public void DebugListenerStatus()
        {
            if (_responseListener == null)
                Debug.Log("Response listener is NULL");
            else
                Debug.Log(
                    $"Response listener status: IsListening={_responseListener.IsListening}");
        }
    }

    [Serializable]
    public class SerializableFunction
    {
        public string name;
        public string description;
        public Parameters parameters;
        public bool neverRespondWithMessage;
    }

    [Serializable]
    public class Parameters
    {
        public List<string> required;
        public string type = "object";
        public Dictionary<string, SerializedArguments> Properties { get; set; }
    }

    [Serializable]
    public class SerializedArguments
    {
        public string type;
        public string description;
    }
}
