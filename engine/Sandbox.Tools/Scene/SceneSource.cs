using System.IO;

namespace Editor;

internal static class SceneSource
{
	internal static Asset FindAsset( SceneFile file )
	{
		if ( file is null )
			return null;

		var asset = AssetSystem.FindByPath( file.ResourcePath );
		if ( file.Guid != System.Guid.Empty && (asset is null || asset.Guid != file.Guid) )
			asset = AssetSystem.All.FirstOrDefault( x => x.Guid == file.Guid );

		if ( asset is not null && file.IsSourceSnapshot && file.ResourcePath != asset.Path )
			file.InitializeSource( asset.Path, asset.Guid );

		return asset;
	}

	internal static SceneFile LoadForEditing( Asset asset )
	{
		var path = asset.GetSourceFile( true );
		var json = File.ReadAllText( path );
		if ( json.StartsWith( '<' ) )
		{
			var kv = NativeEngine.EngineGlue.LoadKeyValues3( json );
			json = NativeEngine.EngineGlue.KeyValues3ToJson( kv.FindOrCreateMember( "data" ) );
			kv.DeleteThis();
		}

		var blobPath = path + "_d";
		var blobs = File.Exists( blobPath ) ? File.ReadAllBytes( blobPath ) : [];
		return SceneFile.FromSource( asset.Path, asset.Guid, json, blobs );
	}

	internal static SceneFile ResolveRuntime( SceneFile file )
	{
		if ( string.IsNullOrEmpty( file.ResourcePath ) )
			return file;

		var asset = FindAsset( file );
		if ( asset is null || !SceneCompileCache.HasCompilation( asset ) )
			return file;

		var editor = SceneEditorSession.Resolve( file );
		if ( editor?.CompilationDirty == true
			|| SceneCompileCache.ReadSetting( asset, SceneCompileCache.DirtyProperty )?.GetValue<bool>() != false )
			return editor is not null ? editor.Scene.CreateSceneFile() : LoadForEditing( asset );

		if ( !SceneCompileCache.ValidateOutput( asset, out var error ) )
		{
			Log.Error( error );
			return null;
		}

		var compiledPath = asset.GetCompiledFile( true );
		if ( string.IsNullOrEmpty( compiledPath ) )
			compiledPath = asset.GetSourceFile( true ) + "_c";

		return SceneFile.FromCompiled( asset.Path, asset.Guid, File.ReadAllBytes( compiledPath ) );
	}

	internal static bool PreparePlay( SceneEditorSession session, out SceneLoadOptions options )
	{
		options = null;
		if ( session.CompilationDirty )
			return true;

		var asset = FindAsset( session.Scene.Source as SceneFile );
		if ( asset is null || !SceneCompileCache.HasCompilation( asset ) )
			return true;

		options = new SceneLoadOptions();
		options.SetScene( session.Scene.Source as SceneFile );
		return options.PrepareRuntime();
	}
}
