using UnityEngine;
using UnityEngine.Rendering;
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Effekseer;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class EffekseerSystem : MonoBehaviour
{
	/// <summary>
	/// シーンビューに描画するかどうか
	/// </summary>
	public bool drawInSceneView = true;

	/// <summary>
	/// エフェクトインスタンスの最大数
	/// </summary>
	public int effectInstances	= 800;

	/// <summary>
	/// 四角形の最大数
	/// </summary>
	public int maxSquares		= 1200;
	
	/// <summary>
	/// サウンドインスタンスの最大数
	/// </summary>
	public int soundInstances	= 16;

	/// <summary>
	/// エフェクトの描画するタイミング
	/// </summary>
	public CameraEvent cameraEvent	= CameraEvent.AfterForwardAlpha;

	/// <summary>
	/// Effekseerのファイルを置く場所
	/// </summary>
	public static string resourcePath
	{
		get {
			return Path.Combine(Application.streamingAssetsPath, "Effekseer");
		}
	}

	/// <summary>
	/// エフェクトの再生
	/// </summary>
	/// <param name="name">エフェクト名</param>
	/// <param name="location">再生開始する位置</param>
	/// <returns>再生したエフェクトのハンドル</returns>
	public static EffekseerHandle PlayEffect(string name, Vector3 location)
	{
		IntPtr effect = Instance._GetEffect(name);
		if (effect != IntPtr.Zero) {
			int handle = Plugin.EffekseerPlayEffect(effect, location.x, location.y, location.z);
			return new EffekseerHandle(handle);
		}
		return new EffekseerHandle(-1);
	}
	
	/// <summary>
	/// 全エフェクトの再生停止
	/// </summary>
	public static void StopAllEffects()
	{
		Plugin.EffekseerStopAllEffects();
	}

	/// <summary>
	/// エフェクトのロード
	/// </summary>
	/// <param name="name">エフェクト名</param>
	public static void LoadEffect(string name)
	{
		Instance._LoadEffect(name);
	}

	/// <summary>
	/// エフェクトの解放
	/// </summary>
	/// <param name="name">エフェクト名</param>
	public static void ReleaseEffect(string name)
	{
		Instance._ReleaseEffect(name);
	}

	#region Internal Implimentation
	
	// シングルトンのインスタンス
	private static EffekseerSystem instance = null;
	public static EffekseerSystem Instance
	{
		get {
			if (instance == null) {
				// staticに無ければ探す
				var system = GameObject.FindObjectOfType<EffekseerSystem>();
				if (system != null) {
					// 有ればstaticにセット
					instance = system;
				} else {
					// 無ければ新しく作成
					var go = GameObject.Find("Effekseer");
					if (go == null) {
						go = new GameObject("Effekseer");
					}
					instance = go.AddComponent<EffekseerSystem>();
				}
			}
			return instance;
		}
	}
	
	// ロードしたエフェクト
	private Dictionary<string, IntPtr> effectList = new Dictionary<string, IntPtr>();
	// ロードしたリソース
	private List<TextureResource> textureList = new List<TextureResource>();
	private List<ModelResource> modelList = new List<ModelResource>();
	private List<SoundResource> soundList = new List<SoundResource>();
	private List<SoundInstance> soundInstanceList = new List<SoundInstance>();
	
#if UNITY_EDITOR
	// ホットリロードの退避用
	private List<string> savedEffectList = new List<string>();
#endif

	// カメラごとのレンダーパス
	class RenderPath {
		public CommandBuffer commandBuffer;
		public CameraEvent cameraEvent;
		public int renderId;
	};
	private Dictionary<Camera, RenderPath> renderPaths = new Dictionary<Camera, RenderPath>();

	private IntPtr _GetEffect(string name) {
		if (effectList.ContainsKey(name)) {
			return effectList[name];
		}
		
		// 存在しなかったらロード
		return _LoadEffect(name);
	}

	private IntPtr _LoadEffect(string name) {
		if (effectList.ContainsKey(name)) {
			return effectList[name];
		}

		// Resourcesから読み込む
		var asset = Resources.Load<TextAsset>(Utility.ResourcePath(name));
		if (asset == null) {
			Debug.LogError("[Effekseer] Failed to load effect: " + name);
			return IntPtr.Zero;
		}
		byte[] bytes = asset.bytes;
		
		GCHandle ghc = GCHandle.Alloc(bytes, GCHandleType.Pinned);
		IntPtr effect = Plugin.EffekseerLoadEffectOnMemory(ghc.AddrOfPinnedObject(), bytes.Length);
		ghc.Free();

		effectList.Add(name, effect);
		return effect;
	}
	
	private void _ReleaseEffect(string name) {
		if (effectList.ContainsKey(name) == false) {
			var effect = effectList[name];
			Plugin.EffekseerReleaseEffect(effect);
			effectList.Remove(name);
		}
	}
	
	void Awake() {
		Plugin.EffekseerInit(effectInstances, maxSquares);
		for (int i = 0; i < soundInstances; i++) {
			GameObject go = new GameObject();
			go.name = "Sound Instance";
			go.transform.parent = transform;
			soundInstanceList.Add(go.AddComponent<SoundInstance>());
		}
	}
	
	void OnDestroy() {
		foreach (var pair in effectList) {
			Plugin.EffekseerReleaseEffect(pair.Value);
		}
		effectList = null;
		Plugin.EffekseerTerm();
	}

	void OnEnable() {
#if UNITY_EDITOR
		Resume();
#endif
		Plugin.EffekseerSetTextureLoaderEvent(
			TextureLoaderLoad, 
			TextureLoaderUnload);
		Plugin.EffekseerSetModelLoaderEvent(
			ModelLoaderLoad, 
			ModelLoaderUnload);
		Plugin.EffekseerSetSoundLoaderEvent(
			SoundLoaderLoad, 
			SoundLoaderUnload);
		Plugin.EffekseerSetSoundPlayerEvent(
			SoundPlayerPlay,
			SoundPlayerStopTag, 
			SoundPlayerPauseTag, 
			SoundPlayerCheckPlayingTag, 
			SoundPlayerStopAll);
		CleanUp();
		Camera.onPreCull += OnPreCullEvent;
	}

	void OnDisable() {
#if UNITY_EDITOR
		Suspend();
#endif
		Camera.onPreCull -= OnPreCullEvent;
		CleanUp();
	}
	
#if UNITY_EDITOR
	void Suspend() {
		// Dictionaryは消えるので文字列にして退避
		foreach (var pair in effectList) {
			savedEffectList.Add(pair.Key + "," + pair.Value.ToString());
		}
		effectList.Clear();
	}
	void Resume() {
		// ホットリロード時はリジューム処理
		foreach (var effect in savedEffectList) {
			string[] tokens = effect.Split(',');
			if (tokens.Length == 2) {
				effectList.Add(tokens[0], (IntPtr)ulong.Parse(tokens[1]));
			}
		}
		savedEffectList.Clear();
	}
#endif

	void CleanUp() {
		// レンダーパスの破棄
		foreach (var pair in renderPaths) {
			var camera = pair.Key;
			var path = pair.Value;
			if (camera != null) {
				camera.RemoveCommandBuffer(path.cameraEvent, path.commandBuffer);
			}
		}
		renderPaths.Clear();
	}
	
	void FixedUpdate() {
		// 1フレーム更新
		Plugin.EffekseerUpdate(1);
	}
	
	void OnPreCullEvent(Camera camera) {
#if UNITY_EDITOR
		if (Array.IndexOf<Camera>(SceneView.GetAllSceneCameras(), camera) >= 0) {
			// シーンビューのカメラはチェック
			if (this.drawInSceneView == false) {
				return;
			}
		} else if (Camera.current.isActiveAndEnabled == false) {
			// シーンビュー以外のエディタカメラは除外
			return;
		}
#endif
		RenderPath path;
		if (renderPaths.ContainsKey(camera)) {
			// レンダーパスが有れば使う
			path = renderPaths[camera];
		} else {
			// 無ければ作成
			path = new RenderPath();
			path.renderId = renderPaths.Count;
			path.cameraEvent = cameraEvent;
			// プラグイン描画するコマンドバッファを作成
			path.commandBuffer = new CommandBuffer();
			path.commandBuffer.IssuePluginEvent(Plugin.EffekseerGetRenderFunc(), path.renderId);
			// コマンドバッファをカメラに登録
			camera.AddCommandBuffer(path.cameraEvent, path.commandBuffer);
			renderPaths.Add(camera, path);
		}

		// ビュー関連の行列を更新
		SetCameraMatrix(path.renderId, camera);
		SetProjectionMatrix(path.renderId, camera);
	}

	private void SetProjectionMatrix(int renderId, Camera camera) {
		float[] projectionMatrixArray = Utility.Matrix2Array(GL.GetGPUProjectionMatrix(
			camera.projectionMatrix, RenderTexture.active));
		GCHandle ghc = GCHandle.Alloc(projectionMatrixArray, GCHandleType.Pinned);
		Plugin.EffekseerSetProjectionMatrix(renderId, ghc.AddrOfPinnedObject());
		ghc.Free();
	}

	private void SetCameraMatrix(int renderId, Camera camera) {
		float[] cameraMatrixArray = Utility.Matrix2Array(camera.worldToCameraMatrix);
		GCHandle ghc = GCHandle.Alloc(cameraMatrixArray, GCHandleType.Pinned);
		Plugin.EffekseerSetCameraMatrix(renderId, ghc.AddrOfPinnedObject());
		ghc.Free();
	}

	[AOT.MonoPInvokeCallbackAttribute(typeof(Plugin.EffekseerTextureLoaderLoad))]
	private static IntPtr TextureLoaderLoad(IntPtr path) {
		var pathstr = Marshal.PtrToStringUni(path);
		var res = new TextureResource();
		if (res.Load(pathstr)) {
			EffekseerSystem.Instance.textureList.Add(res);
			return res.GetNativePtr();
		}
		return IntPtr.Zero;
	}
	[AOT.MonoPInvokeCallbackAttribute(typeof(Plugin.EffekseerTextureLoaderUnload))]
	private static void TextureLoaderUnload(IntPtr path) {
		var pathstr = Marshal.PtrToStringUni(path);
		foreach (var res in EffekseerSystem.Instance.textureList) {
			if (res.Path == pathstr) {
				EffekseerSystem.Instance.textureList.Remove(res);
				return;
			}
		}
	}
	[AOT.MonoPInvokeCallbackAttribute(typeof(Plugin.EffekseerModelLoaderLoad))]
	private static int ModelLoaderLoad(IntPtr path, IntPtr buffer, int bufferSize) {
		var pathstr = Marshal.PtrToStringUni(path);
		var res = new ModelResource();
		if (res.Load(pathstr) && res.Copy(buffer, bufferSize)) {
			EffekseerSystem.Instance.modelList.Add(res);
			return res.ModelData.bytes.Length;
		}
		return 0;
	}
	[AOT.MonoPInvokeCallbackAttribute(typeof(Plugin.EffekseerModelLoaderUnload))]
	private static void ModelLoaderUnload(IntPtr path) {
		var pathstr = Marshal.PtrToStringUni(path);
		foreach (var res in EffekseerSystem.Instance.modelList) {
			if (res.Path == pathstr) {
				EffekseerSystem.Instance.modelList.Remove(res);
				return;
			}
		}
	}
	[AOT.MonoPInvokeCallbackAttribute(typeof(Plugin.EffekseerSoundLoaderLoad))]
	private static int SoundLoaderLoad(IntPtr path) {
		var pathstr = Marshal.PtrToStringUni(path);
		var res = new SoundResource();
		if (res.Load(pathstr)) {
			EffekseerSystem.Instance.soundList.Add(res);
			return EffekseerSystem.Instance.soundList.Count;
		}
		return 0;
	}
	[AOT.MonoPInvokeCallbackAttribute(typeof(Plugin.EffekseerSoundLoaderUnload))]
	private static void SoundLoaderUnload(IntPtr path) {
		var pathstr = Marshal.PtrToStringUni(path);
		foreach (var res in EffekseerSystem.Instance.soundList) {
			if (res.Path == pathstr) {
				EffekseerSystem.Instance.soundList.Remove(res);
				return;
			}
		}
	}
	
	[AOT.MonoPInvokeCallbackAttribute(typeof(Plugin.EffekseerSoundPlayerPlay))]
	private static void SoundPlayerPlay(IntPtr tag, 
			int data, float volume, float pan, float pitch, 
			bool mode3D, float x, float y, float z, float distance) {
		EffekseerSystem.Instance.PlaySound(tag, data, volume, pan, pitch, mode3D, x, y, z, distance);
	}
	[AOT.MonoPInvokeCallbackAttribute(typeof(Plugin.EffekseerSoundPlayerStopTag))]
	private static void SoundPlayerStopTag(IntPtr tag) {
		EffekseerSystem.Instance.StopSound(tag);
	}
	[AOT.MonoPInvokeCallbackAttribute(typeof(Plugin.EffekseerSoundPlayerPauseTag))]
	private static void SoundPlayerPauseTag(IntPtr tag, bool pause) {
		EffekseerSystem.Instance.PauseSound(tag, pause);
	}
	[AOT.MonoPInvokeCallbackAttribute(typeof(Plugin.EffekseerSoundPlayerCheckPlayingTag))]
	private static bool SoundPlayerCheckPlayingTag(IntPtr tag) {
		return EffekseerSystem.Instance.CheckSound(tag);
	}
	[AOT.MonoPInvokeCallbackAttribute(typeof(Plugin.EffekseerSoundPlayerStopAll))]
	private static void SoundPlayerStopAll() {
		EffekseerSystem.Instance.StopAllSounds();
	}

	private void PlaySound(IntPtr tag, 
		int data, float volume, float pan, float pitch, 
		bool mode3D, float x, float y, float z, float distance)
	{
		if (data <= 0) {
			return;
		}
		SoundResource resource = soundList[data - 1];
		if (resource == null) {
			return;
		}
		foreach (var instance in soundInstanceList) {
			if (!instance.CheckPlaying()) {
				instance.Play(tag.ToString(), resource.Audio, volume, pan, pitch, mode3D, x, y, z, distance);
				break;
			}
		}
	}
	private void StopSound(IntPtr tag) {
		foreach (var sound in soundInstanceList) {
			if (sound.AudioTag == tag.ToString()) {
				sound.Stop();
			}
		}
	}
	private void PauseSound(IntPtr tag, bool paused) {
		foreach (var sound in soundInstanceList) {
			if (sound.AudioTag == tag.ToString()) {
				sound.Pause(paused);
			}
		}
	}
	private bool CheckSound(IntPtr tag) {
		bool playing = false;
		foreach (var sound in soundInstanceList) {
			if (sound.AudioTag == tag.ToString()) {
				playing |= sound.CheckPlaying();
			}
		}
		return playing;
	}
	private void StopAllSounds() {
		foreach (var sound in soundInstanceList) {
			sound.Stop();
		}
	}

	#endregion
}

/// <summary>
/// 再生したエフェクトのインスタンスハンドル
/// </summary>
public struct EffekseerHandle
{
	private int m_handle;
	private bool m_paused;
	private bool m_shown;

	public EffekseerHandle(int handle)
	{
		m_handle = handle;
		m_paused = false;
		m_shown = false;
	}
	
	/// <summary>
	/// エフェクトを停止する
	/// </summary>
	public void Stop()
	{
		Plugin.EffekseerStopEffect(m_handle);
	}
	
	/// <summary>
	/// エフェクトの位置を設定
	/// </summary>
	/// <param name="location">位置</param>
	public void SetLocation(Vector3 location)
	{
		Plugin.EffekseerSetLocation(m_handle, location.x, location.y, location.z);
	}
	
	/// <summary>
	/// エフェクトの回転を設定
	/// </summary>
	/// <param name="rotation">回転</param>
	public void SetRotation(Quaternion rotation)
	{
		Vector3 axis;
		float angle;
		rotation.ToAngleAxis(out angle, out axis);
		Plugin.EffekseerSetRotation(m_handle, axis.x, axis.y, axis.z, angle * Mathf.Deg2Rad);
	}
	
	/// <summary>
	/// エフェクトの拡縮を設定
	/// </summary>
	/// <param name="scale">拡縮</param>
	public void SetScale(Vector3 scale)
	{
		Plugin.EffekseerSetScale(m_handle, scale.x, scale.y, scale.z);
	}

	/// <summary>
	/// Update時に更新するか
	/// </summary>
	public bool paused
	{
		set {
			Plugin.EffekseerSetPaused(m_handle, value);
			m_paused = value;
		}
		get {
			return m_paused;
		}
	}
	
	/// <summary>
	/// Draw時で描画されるか
	/// </summary>
	public bool shown
	{
		set {
			Plugin.EffekseerSetShown(m_handle, value);
			m_shown = value;
		}
		get {
			return m_shown;
		}
	}
	
	/// <summary>
	/// ハンドルが有効かどうか<br/>
	/// <para>true:有効</para>
	/// <para>false:無効</para>
	/// </summary>
	public bool enable
	{
		get {
			return m_handle >= 0;
		}
	}
	
	/// <summary>
	/// エフェクトのインスタンスが存在しているかどうか
	/// <para>true:存在している</para>
	/// <para>false:再生終了で破棄。もしくはStopで停止された</para>
	/// </summary>
	public bool exists
	{
		get {
			return Plugin.EffekseerExists(m_handle);
		}
	}
}