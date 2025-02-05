using System;
using UnityEngine;
using Unity.Netcode;
using Unity.Services.Core;
using Unity.Services.Relay;
using System.Threading.Tasks;
using Unity.Services.Lobbies;
using System.Collections.Generic;
using UnityEngine.SceneManagement;
using Unity.Services.Relay.Models;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Lobbies.Models;
using Unity.Networking.Transport.Relay;

#if UNITY_EDITOR
using ParrelSync;
#endif

internal sealed class GameCoordinator : MonoBehaviour
{
    public enum MonopolyScene : byte
    {
        Bootstrap,
        MainMenu,
        GameLobby,
        MonopolyGame
    }

#if UNITY_WEBGL
    private const string CONNECTION_TYPE = "wss";
#else
    private const string CONNECTION_TYPE = "dtls";
#endif


    public static GameCoordinator Instance { get; private set; }

    private Scene activeScene;
    private int initializationCount;
    private LinkedList<Type> objectsToLoad;
    private LinkedList<Type> initializedObjects;

    public event Action OnAuthenticationFailed;
    public event Action<RelayServiceException> OnEstablishingConnectionRelayFailed;
    public event Action<LobbyServiceException> OnEstablishingConnectionLobbyFailed;

    public bool IsGameQuiting { get; private set; }
    public Player LocalPlayer { get; private set; }
    public MonopolyScene ActiveScene { get; private set; }

    private void Awake()
    {
        if (Instance != null)
            throw new System.InvalidOperationException($"Singleton {this.GetType().FullName} has already been initialized.");

        Instance = this;
        UnityEngine.Object.DontDestroyOnLoad(this.gameObject);
    }

    private void OnEnable()
    {
        SceneManager.activeSceneChanged += this.HandleActiveSceneChanged;
    }

    private void OnDisable()
    {
        SceneManager.activeSceneChanged -= this.HandleActiveSceneChanged;
    }

    private async void Start()
    {
        this.objectsToLoad = new LinkedList<Type>();
        this.initializedObjects = new LinkedList<Type>();

        try
        {
#if UNITY_EDITOR
            InitializationOptions options = new InitializationOptions();
            options.SetProfile(ClonesManager.IsClone() ? ClonesManager.GetArgument() : "Primary");
            await UnityServices.InitializeAsync(options);
#else
            await UnityServices.InitializeAsync();
#endif
            await AuthenticationService.Instance.SignInAnonymouslyAsync();

            this.InitializeLocalPlayer(PlayerPrefs.GetString(LobbyManager.KEY_PLAYER_NICKNAME));
        }
        catch
        {
            this.OnAuthenticationFailed?.Invoke();
            return;
        }

        await this.LoadSceneAsync(GameCoordinator.MonopolyScene.MainMenu);
    }

    private void OnApplicationQuit()
    {
        this.IsGameQuiting = true;
    }

    #region Updating Player

    public void UpdateLocalPlayer(string newNickname)
    {
        newNickname = newNickname.Trim();

        this.LocalPlayer.Data[LobbyManager.KEY_PLAYER_NICKNAME].Value = newNickname;

        PlayerPrefs.SetString(LobbyManager.KEY_PLAYER_NICKNAME, newNickname);
        PlayerPrefs.Save();
    }

    public void InitializeLocalPlayer(string nickname)
    {
        nickname = nickname.Trim();

        Player player = new Player(AuthenticationService.Instance.PlayerId)
        {
            Data = new Dictionary<string, PlayerDataObject>
            {
                { LobbyManager.KEY_PLAYER_NICKNAME, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, nickname) },
                { LobbyManager.KEY_PLAYER_SCENE, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, GameCoordinator.Instance.ActiveScene.ToString()) }
            }
        };

        PlayerPrefs.SetString(LobbyManager.KEY_PLAYER_NICKNAME, nickname);
        PlayerPrefs.Save();

        this.LocalPlayer = player;
    }

    #endregion

    #region Scenes Management

    public void LoadSceneNetwork(MonopolyScene scene)
    {
        NetworkManager.Singleton.SceneManager.LoadScene(scene.ToString(), LoadSceneMode.Single);
    }

    public async Task LoadSceneAsync(MonopolyScene scene)
    {
        Debug.Log($"Loading scene: {scene}");
        await SceneManager.LoadSceneAsync(scene.ToString(), LoadSceneMode.Single);
    }

    public void UpdateInitializedObjects(Type gameObject)
    {
        if (!this.objectsToLoad.Contains(gameObject) || this.initializedObjects.Contains(gameObject))
        {
            throw new System.ArgumentException($"{nameof(gameObject)} is either not in the load list or already initialized.");
        }

        this.initializedObjects.AddLast(gameObject);

        if (this.initializedObjects.Count == this.objectsToLoad.Count)
        {
            LobbyManager.Instance?.UpdateLocalPlayerData();
        }
    }

    public void SetupInitializedObjects(params Type[] gameObjectsToLoad)
    {
        foreach (Type gameObject in gameObjectsToLoad)
        {
            this.objectsToLoad.AddLast(gameObject);
        }
    }

    private void HandleActiveSceneChanged(Scene previousActiveScene, Scene newActiveScene)
    {
        this.objectsToLoad?.Clear();
        this.initializedObjects?.Clear();

        this.activeScene = SceneManager.GetActiveScene();

        switch (newActiveScene.name)
        {
            case nameof(GameCoordinator.MonopolyScene.MainMenu):
                this.ActiveScene = GameCoordinator.MonopolyScene.MainMenu;
                break;
            case nameof(GameCoordinator.MonopolyScene.GameLobby):
                {
                    this.ActiveScene = GameCoordinator.MonopolyScene.GameLobby;

                    this.SetupInitializedObjects(typeof(UIManagerGameLobby), typeof(ObjectPoolPanelPlayerLobby));

                    LobbyManager.Instance?.OnGameLobbyLoaded?.Invoke();
                }
                break;
            case nameof(GameCoordinator.MonopolyScene.MonopolyGame):
                {
                    this.ActiveScene = GameCoordinator.MonopolyScene.MonopolyGame;

                    this.SetupInitializedObjects(typeof(GameManager), typeof(MonopolyBoard), typeof(UIManagerMonopolyGame));

                    LobbyManager.Instance?.OnMonopolyGameLoaded?.Invoke();
                }
                break;
        }
    }

    #endregion

    #region Establishing Connection

    public async Task HostLobbyAsync()
    {
        if (this.LocalPlayer == null)
        {
            Debug.LogError("LocalPlayer is null. Cannot host a lobby.");
            return;
        }

        try
        {
            Allocation hostAllocation = await RelayService.Instance.CreateAllocationAsync(LobbyManager.MAX_PLAYERS);

            RelayServerData relayServerData = new RelayServerData(hostAllocation, CONNECTION_TYPE);

            NetworkManager.Singleton?.GetComponent<UnityTransport>().SetRelayServerData(relayServerData);

            string relayCode = await RelayService.Instance.GetJoinCodeAsync(hostAllocation.AllocationId);
            Debug.Log($"Relay Join Code: {relayCode}");

            await LobbyManager.Instance?.HostLobbyAsync(relayCode);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to host lobby: {ex.Message}");
            this.OnEstablishingConnectionRelayFailed?.Invoke(ex as RelayServiceException);
        }
    }

    public async Task ConnectLobbyAsync(string joinCode)
    {
        if (string.IsNullOrEmpty(joinCode))
        {
            Debug.LogError("JoinCode is invalid or empty.");
            return;
        }

        try
        {
            Debug.Log($"Connecting to Relay with JoinCode: {joinCode}");

            JoinAllocation clientAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);

            RelayServerData relayServerData = new RelayServerData(clientAllocation, CONNECTION_TYPE);

            NetworkManager.Singleton?.GetComponent<UnityTransport>().SetRelayServerData(relayServerData);

            Debug.Log("Connecting to Lobby...");
            await LobbyManager.Instance?.ConnectLobbyAsync(joinCode);

            Debug.Log("Successfully connected to the Lobby.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to connect to lobby: {ex.Message}");
            this.OnEstablishingConnectionLobbyFailed?.Invoke(ex as LobbyServiceException);
        }
    }

    #endregion
}
