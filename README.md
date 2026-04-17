# Inspector History for Unity

<img width="654" height="554" alt="Inspector History" src="https://github.com/user-attachments/assets/7fc5046f-ca03-4e0c-a77c-997c6b0d40a8" />

  Inspector History is a Unity editor productivity tool that tracks your recently visited assets and scene objects as a
  navigable selection history — like a browser's back/forward for your Inspector. Quickly jump between recently selected
   objects, pin bookmarks to a favorites bar for quick access, and filter by type. 
   
   Unity 6+. It should work for earlier versions, but that's not been tested.

  ## Features

  - **Session persistence** — history and favorites survive domain reloads, play mode, and editor restarts
  - **Back / Forward navigation** — step through your selection history; ALT+click jumps to the start or end
  - **Keyboard shortcuts** — `ALT + ←` / `ALT + →` for back/forward without leaving the keyboard
  - **Pinned favorites** — up to 15 slots across configurable rows; left-click to select, middle-click to clear, full
  drag & drop support in both directions. For instance, drag a object from Favorites to an Inspector field.
  - **Type filters** — independently toggle Scene objects, ScriptableObjects, and Prefabs; disabling a type purges
  existing entries
  - **Color-coded type tags** — each history entry shows a labeled color strip (Mat, Prefab, S.O., Script, Tex, etc.)
  - **Reveal in Finder** — one-click OS file browser reveal for any asset entry
  - **Undo** — step back through up to 6 history snapshots
  - **Configurable sizes** — history depth (6–50) and favorites count (3–15) saved per-machine
  - **Zero dependencies** — single UPM package, no third-party requirements

  ## Installation
  **Via Package Manager (recommended)**

  1. Open your Unity project and go to **Window → Package Manager**
  2. Click the **+** button in the top-left corner of the Package Manager window
  3. Select **Add package from git URL...**
  4. Paste in the following URL and click **Add**:
     https://github.com/CorgiCabal/InspectorHistory.git
  5. Unity will download and import the package automatically

  **Manually**

  Open `Packages/manifest.json` in your project (it sits next to your `Assets` folder) and add the following line inside
   the `"dependencies"` block:

  ```json
  "com.corgicabal.inspector-history": "https://github.com/CorgiCabal/InspectorHistory.git"
  ```
  Save the file — Unity will detect the change and import the package automatically.

  ---
  Once installed, open the window via Window → Inspector History.

  ## Attribution
  Linking back to this repo is appreciated but not required.

  <!-- unity selection history, unity inspector history, unity editor navigation, back button for Unity Editor, unity recently selected objects, unity
   inspector bookmarks, unity asset favorites, unity back forward navigation, unity selection tracker, unity quick navigation, unity project navigation -->
