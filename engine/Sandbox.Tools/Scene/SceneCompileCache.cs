using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Sandbox.Resources;

namespace Editor;

/// <summary>
/// Explicit scene compilations belong to the source asset, not to another editable scene. The manifest
/// selects an immutable generation; source metadata retains compilation intent if generated data is deleted.
/// </summary>
internal static class SceneCompileCache
{
	const int Version = 7;
	const string Missing = "missing";
	const string SceneJson = ".scene.json";
	const string SceneBlob = ".scene.blob";
	const string OwnershipFile = ".scene-compile-generation";
	const string Ownership = "sbox-scene-compile:1";
	const string RequiredProperty = "sceneCompileRequired";
	internal const string DirtyProperty = "sceneCompileDirty";
	const string GenerationProperty = "__scene_compile_generation";
	static readonly JsonSerializerOptions JsonOptions = new( JsonSerializerOptions.Default ) { MaxDepth = 512 };

	sealed class Compilation
	{
		public int Version { get; set; }
		public Guid SceneId { get; set; }
		public string Generation { get; set; }
		public Dictionary<string, string> Outputs { get; set; }
	}

	static string SourcePath( Asset asset )
	{
		var source = asset.GetSourceFile( true );
		if ( !string.IsNullOrEmpty( source )
			&& (File.Exists( source ) || File.Exists( ManifestPath( source ) )) )
			return source;

		var compiled = asset.GetCompiledFile( true );
		if ( string.IsNullOrEmpty( compiled ) || !compiled.EndsWith( ".scene_c", StringComparison.OrdinalIgnoreCase ) )
			return null;

		source = compiled[..^2];
		return File.Exists( ManifestPath( source ) ) ? source : null;
	}

	static bool IsScene( Asset asset ) => asset?.AssetType?.FileExtension == "scene";
	static string DataFolder( string source ) => Path.ChangeExtension( source, null ) + "_scene_data";
	static string ManifestPath( string source ) => Path.Combine( DataFolder( source ), "compiled", ".scene-compile.json" );
	static string MetadataPath( string source ) => source + ".meta";
	static string OutputPath( string source, Compilation compilation, string name ) => Path.Combine( DataFolder( source ), "compiled", compilation.Generation, name );
	static string Error( string source, string reason ) => $"Scene compilation for '{source}' {reason}. Save the scene, then use Scene > Compile Scene to update its runtime data.";

	internal static void BeginGeneration( Asset asset, string generation )
	{
		if ( !Guid.TryParseExact( generation, "N", out _ ) )
			throw new ArgumentException( "Invalid scene compilation generation.", nameof( generation ) );

		var compilation = new Compilation { Generation = generation };
		WriteAtomic( OutputPath( SourcePath( asset ), compilation, OwnershipFile ), Encoding.UTF8.GetBytes( Ownership ) );
	}

	internal static void DiscardGeneration( string source, string generation )
	{
		if ( !Guid.TryParseExact( generation, "N", out _ ) )
			throw new ArgumentException( "Invalid scene compilation generation.", nameof( generation ) );

		var marker = OutputPath( source, new Compilation { Generation = generation }, OwnershipFile );
		var folder = Path.GetDirectoryName( marker );

		try
		{
			if ( !Directory.Exists( folder ) )
				return;

			// A failed rollback may still leave this generation selected. Never delete its data.
			var manifest = ManifestPath( source );
			if ( File.Exists( manifest ) )
			{
				var current = JsonSerializer.Deserialize<Compilation>( File.ReadAllText( manifest ), JsonOptions )
					?? throw new InvalidDataException( "The scene compilation manifest is invalid." );
				if ( !Guid.TryParseExact( current.Generation, "N", out _ ) )
					throw new InvalidDataException( "The scene compilation manifest has an invalid generation." );
				if ( current.Generation.Equals( generation, StringComparison.OrdinalIgnoreCase ) )
					return;
			}

			DiscardOwnedGeneration( folder );
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException )
		{
			Log.Warning( $"Could not remove scene compilation '{folder}': {e.Message}" );
		}
	}

	static void DiscardOwnedGeneration( string folder )
	{
		try
		{
			if ( (File.GetAttributes( folder ) & FileAttributes.ReparsePoint) != 0 )
				return;

			var marker = Path.Combine( folder, OwnershipFile );
			if ( !File.Exists( marker ) || File.ReadAllText( marker ) != Ownership )
				return;

			try
			{
				Directory.Delete( folder, recursive: true );
			}
			finally
			{
				if ( Directory.Exists( folder ) && !File.Exists( marker ) )
					WriteAtomic( marker, Encoding.UTF8.GetBytes( Ownership ) );
			}
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			Log.Warning( $"Could not remove scene compilation '{folder}': {e.Message}" );
		}
	}

	internal static void PruneGenerations( string source, bool retired = false )
	{
		var folder = Path.GetDirectoryName( ManifestPath( source ) );

		try
		{
			var manifest = ManifestPath( source );
			Compilation current = null;
			if ( !retired || File.Exists( manifest ) )
			{
				current = JsonSerializer.Deserialize<Compilation>( File.ReadAllText( manifest ), JsonOptions );
				if ( current is null || !Guid.TryParseExact( current.Generation, "N", out _ ) )
					throw new InvalidDataException( "The scene compilation manifest has an invalid generation." );
			}

			if ( !Directory.Exists( folder ) )
				return;

			foreach ( var directory in Directory.GetDirectories( folder ) )
			{
				var generation = Path.GetFileName( directory );
				if ( !Guid.TryParseExact( generation, "N", out _ )
					|| generation.Equals( current?.Generation, StringComparison.OrdinalIgnoreCase ) )
					continue;

				DiscardOwnedGeneration( directory );
			}
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException )
		{
			Log.Warning( $"Could not clean old scene compilations in '{folder}': {e.Message}" );
		}
	}

	/// <summary>
	/// Only compiler-owned generations are filtered. Other assets in the scene data folder remain
	/// ordinary publishable content. Source packages also omit compiled runtime scenes.
	/// </summary>
	internal static bool ShouldPublishFile( string path, bool sourcePackage )
	{
		if ( string.IsNullOrEmpty( path ) )
			return true;

		if ( sourcePackage && path.EndsWith( ".scene_c", StringComparison.OrdinalIgnoreCase )
			&& File.Exists( path[..^2] ) && HasHistory( path[..^2] ) )
			return false;

		var generationFolder = Directory.GetParent( Path.GetFullPath( path ) );
		if ( generationFolder?.Parent?.Parent is not { } sceneFolder
			|| !Guid.TryParseExact( generationFolder.Name, "N", out _ )
			|| !generationFolder.Parent.Name.Equals( "compiled", StringComparison.OrdinalIgnoreCase )
			|| !sceneFolder.Name.EndsWith( "_scene_data", StringComparison.OrdinalIgnoreCase ) )
			return true;

		var marker = Path.Combine( generationFolder.FullName, OwnershipFile );
		var cache = Path.Combine( generationFolder.FullName, SceneJson );
		var owned = File.Exists( marker ) && File.ReadAllText( marker ) == Ownership;
		if ( !owned && File.Exists( cache ) )
			owned = ReadSourceJson( cache )?["__scene_compiled"]?.GetValue<bool>() == true;
		if ( !owned )
			return true;

		if ( sourcePackage || Path.GetFileName( path ).StartsWith( '.' ) )
			return false;

		var source = sceneFolder.FullName[..^"_scene_data".Length] + ".scene";
		if ( !File.Exists( ManifestPath( source ) ) )
			return false;

		var compilation = JsonSerializer.Deserialize<Compilation>( File.ReadAllText( ManifestPath( source ) ), JsonOptions );
		if ( compilation is null || compilation.Version != Version || compilation.Outputs is null || !Guid.TryParseExact( compilation.Generation, "N", out _ ) )
			throw new InvalidDataException( Error( source, "has an invalid or incompatible manifest" ) );

		var name = Path.GetFileName( path );
		return generationFolder.Name.Equals( compilation.Generation, StringComparison.OrdinalIgnoreCase )
			&& (compilation.Outputs.ContainsKey( name ) || !File.Exists( path ) && compilation.Outputs.ContainsKey( name + "_c" ));
	}

	static JsonNode ReadSourceJson( string path )
	{
		var json = File.ReadAllText( path );
		if ( json.StartsWith( '<' ) )
		{
			var kv = NativeEngine.EngineGlue.LoadKeyValues3( json );
			try
			{
				json = NativeEngine.EngineGlue.KeyValues3ToJson( kv.FindOrCreateMember( "data" ) );
			}
			finally
			{
				kv.DeleteThis();
			}
		}

		return JsonNode.Parse( json, default, new JsonDocumentOptions { MaxDepth = 512, CommentHandling = JsonCommentHandling.Skip } );
	}

	static string CompiledPath( string source )
	{
		var path = AssetSystem.FindByPath( source )?.GetCompiledFile( true );
		return string.IsNullOrEmpty( path ) ? source + "_c" : path;
	}

	static JsonObject ReadCompiledJson( string source, out byte[] data )
	{
		data = null;
		var path = CompiledPath( source );
		if ( !File.Exists( path ) )
			return null;

		data = File.ReadAllBytes( path );
		if ( data.Length == 0 )
			throw new InvalidDataException( "has an empty compiled resource" );

		var json = Game.Resources.ReadCompiledResourceJson( data );
		return JsonNode.Parse( json, default, new JsonDocumentOptions { MaxDepth = 512 } ) as JsonObject
			?? throw new InvalidDataException( "has invalid compiled scene data" );
	}

	static bool HasHistory( string source )
	{
		var metadata = ReadMetadata( ReadMetadataBytes( source ) );
		if ( metadata[RequiredProperty]?.GetValue<bool>() == true )
			return true;

		if ( File.Exists( ManifestPath( source ) ) )
			return true;

		try
		{
			// Only an actual compiled marker proves history when the data folder has been deleted.
			return ReadCompiledJson( source, out _ )?["__scene_compiled"]?.GetValue<bool>() == true;
		}
		catch ( Exception e ) when ( e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or FormatException )
		{
			Log.Warning( $"Cannot read ordinary compiled scene '{source}': {e.Message}. Recompile it from source." );
			return false;
		}
	}

	static byte[] ReadMetadataBytes( string source ) => File.Exists( MetadataPath( source ) ) ? File.ReadAllBytes( MetadataPath( source ) ) : null;

	static JsonObject ReadMetadata( byte[] bytes )
	{
		if ( bytes is null )
			return new JsonObject();

		return JsonNode.Parse( bytes, default, new JsonDocumentOptions
		{
			AllowTrailingCommas = true,
			CommentHandling = JsonCommentHandling.Skip
		} ) as JsonObject ?? throw new InvalidDataException( "Scene metadata must contain a JSON object" );
	}

	internal static bool IsDirty( Asset asset ) => ReadSetting( asset, DirtyProperty )?.GetValue<bool>() != false;

	internal static JsonNode ReadSetting( Asset asset, string name )
	{
		var source = IsScene( asset ) ? SourcePath( asset ) : null;
		if ( string.IsNullOrEmpty( source ) )
			throw new InvalidDataException( "Scene compilation settings require a saved source scene." );

		return ReadMetadata( ReadMetadataBytes( source ) )[name];
	}

	internal static void WriteSetting( Asset asset, string name, JsonNode value )
	{
		var source = IsScene( asset ) ? SourcePath( asset ) : null;
		if ( string.IsNullOrEmpty( source ) || !File.Exists( source ) )
			throw new InvalidDataException( "Scene compilation settings require a saved source scene." );

		var metadata = ReadMetadata( ReadMetadataBytes( source ) );
		if ( JsonNode.DeepEquals( metadata[name], value ) )
			return;

		metadata[name] = value?.DeepClone();
		WriteAtomic( MetadataPath( source ), JsonSerializer.SerializeToUtf8Bytes( metadata, JsonOptions ) );
	}

	/// <summary>
	/// True when there is persisted compilation history, including a missing or damaged current compilation.
	/// Compiled-only packaged scenes do not need their editor cache.
	/// </summary>
	internal static bool HasCompilation( Asset asset )
	{
		if ( !IsScene( asset ) || string.IsNullOrEmpty( SourcePath( asset ) ) )
			return false;

		var source = SourcePath( asset );
		try
		{
			return HasHistory( source );
		}
		catch ( Exception e ) when ( e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or FormatException )
		{
			// Route unreadable output through ValidateOutput rather than silently loading it.
			Log.Warning( Error( source, e.Message ) );
			return true;
		}
	}

	/// <summary>
	/// Known compilations must have complete, matching output. Authoring changes do not invalidate a bake.
	/// </summary>
	internal static bool ValidateOutput( Asset asset, out string error )
	{
		error = null;
		return !IsScene( asset ) || string.IsNullOrEmpty( SourcePath( asset ) )
			|| TryRead( SourcePath( asset ), out _, out error, requireCompiled: true );
	}

	static bool TryRead( string source, out Compilation compilation, out string error, bool requireCompiled = false )
	{
		compilation = null;
		error = null;

		try
		{
			if ( !HasHistory( source ) )
				return true;

			if ( !File.Exists( ManifestPath( source ) ) )
				throw new InvalidDataException( "is missing its generated cache" );

			compilation = JsonSerializer.Deserialize<Compilation>( File.ReadAllText( ManifestPath( source ) ), JsonOptions );
			if ( compilation is null || compilation.Version != Version || !Guid.TryParseExact( compilation.Generation, "N", out _ )
				|| compilation.Outputs is null
				|| !compilation.Outputs.ContainsKey( SceneJson ) || !compilation.Outputs.ContainsKey( SceneBlob ) )
				throw new InvalidDataException( "has an invalid or incompatible manifest" );

			foreach ( var (name, hash) in compilation.Outputs )
			{
				if ( Path.GetFileName( name ) != name || OutputHash( OutputPath( source, compilation, name ) ) != hash || hash == Missing )
					throw new InvalidDataException( $"is missing or has changed generated data '{name}'" );
			}

			RequireSceneIdentity( source, compilation.SceneId );

			if ( requireCompiled )
			{
				var runtime = ReadCompiledJson( source, out var data );
				if ( runtime?[GenerationProperty]?.GetValue<string>() != compilation.Generation
					|| runtime["__guid"]?.GetValue<Guid>() != compilation.SceneId
					|| runtime["__scene_compiled"]?.GetValue<bool>() != true
					|| !string.IsNullOrEmpty( runtime["__scene_compile_error"]?.GetValue<string>() ) )
					throw new InvalidDataException( "is missing its matching runtime .scene_c" );

				var blob = Game.Resources.ReadCompiledResourceBlock( BlobDataSerializer.CompiledBlobName, data ) ?? [];
				if ( Convert.ToHexString( SHA256.HashData( blob ) ) != compilation.Outputs[SceneBlob] )
					throw new InvalidDataException( "has runtime binary data that does not match its compilation" );
			}

			return true;
		}
		catch ( Exception e ) when ( e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or FormatException )
		{
			compilation = null;
			error = e.Message.StartsWith( "Scene compilation for ", StringComparison.Ordinal ) ? e.Message : Error( source, e.Message.TrimEnd( '.' ) );
			return false;
		}
	}

	/// <summary>
	/// Called only by the ordinary resource compiler. Never generates geometry or rewrites source.
	/// </summary>
	internal static bool TryGetRuntimeData( ResourceCompileContext context, ref string json, out byte[] blob, out bool compiled )
	{
		blob = null;
		compiled = false;
		var source = context.AbsolutePath;
		if ( !TryRead( source, out var compilation, out var error ) )
		{
			// Saving must still produce a valid resource container. Runtime loading rejects this
			// explicit unavailable state; failing compilation here causes endless on-demand retries.
			Log.Error( error );
			var sourceJson = ReadSourceJson( source );
			var unavailable = new JsonObject
			{
				["__guid"] = (sourceJson?["__guid"] ?? sourceJson?["Id"])?.DeepClone(),
				["__scene_compiled"] = true,
				["__scene_compile_error"] = error
			};
			json = unavailable.ToJsonString( JsonOptions );
			blob = [];
			compiled = true;
			return true;
		}

		if ( compilation is null )
			return true;

		foreach ( var name in compilation.Outputs.Keys )
		{
			// Compiled resources live in GAME, while AddCompileReference records CONTENT inputs.
			// Register them as runtime resources rather than nonexistent source-side binary files.
			if ( name.EndsWith( "_c", StringComparison.OrdinalIgnoreCase ) )
			{
				var resource = Path.Combine( DataFolder( context.RelativePath ), "compiled", compilation.Generation, name[..^2] );
				context.AddRuntimeReference( resource.NormalizeFilename( false ) );
			}
		}

		context.AddCompileReference( OutputPath( source, compilation, SceneJson ) );
		context.AddCompileReference( OutputPath( source, compilation, SceneBlob ) );
		var jsonBytes = File.ReadAllBytes( OutputPath( source, compilation, SceneJson ) );
		blob = File.ReadAllBytes( OutputPath( source, compilation, SceneBlob ) );
		if ( Convert.ToHexString( SHA256.HashData( jsonBytes ) ) != compilation.Outputs[SceneJson]
			|| Convert.ToHexString( SHA256.HashData( blob ) ) != compilation.Outputs[SceneBlob] )
		{
			Log.Error( Error( source, "changed while its runtime data was being read" ) );
			return false;
		}

		var runtime = JsonNode.Parse( jsonBytes, default, new JsonDocumentOptions { MaxDepth = 512 } );
		var assets = new Dictionary<Guid, Asset>();
		foreach ( var asset in AssetSystem.All )
			assets.TryAdd( asset.Guid, asset );
		ResolveRuntimeReferences( runtime, assets );
		json = runtime.ToJsonString( JsonOptions );
		compiled = true;
		return true;
	}

	static Asset ResolveGuidReference( JsonObject obj, Dictionary<Guid, Asset> assets )
	{
		if ( !obj.All( x => x.Key is "Id" or "Path" )
			|| obj["Id"] is not JsonValue idValue || !idValue.TryGetValue<string>( out var idText )
			|| !Guid.TryParse( idText, out var id ) || id == Guid.Empty )
			return null;

		return assets.GetValueOrDefault( id )
			?? (obj["Path"] is JsonValue path && path.TryGetValue<string>( out var filename ) ? AssetSystem.FindByPath( filename ) : null)
			?? throw new InvalidDataException( $"Runtime resource '{id}' could not be resolved; restore it or compile the scene again" );
	}

	static void ResolveRuntimeReferences( JsonNode node, Dictionary<Guid, Asset> assets )
	{
		if ( node is JsonObject obj )
		{
			if ( ResolveGuidReference( obj, assets ) is { } reference )
			{
				// ScanJson discovers runtime dependencies by path, not GUID. Supply the resolved
				// path so GUID-only and moved resources are also included when publishing.
				obj["Path"] = reference.Path;
				return;
			}

			foreach ( var child in obj )
				ResolveRuntimeReferences( child.Value, assets );
		}
		else if ( node is JsonArray array )
		{
			foreach ( var child in array )
				ResolveRuntimeReferences( child, assets );
		}
	}

	static string OutputHash( string path )
	{
		if ( !File.Exists( path ) )
			return Missing;

		using var stream = File.OpenRead( path );
		return Convert.ToHexString( SHA256.HashData( stream ) );
	}

	static void RequireSceneIdentity( string source, Guid id )
	{
		var json = ReadSourceJson( source );
		if ( id == Guid.Empty || (json?["__guid"] ?? json?["Id"])?.GetValue<Guid>() != id )
			throw new InvalidDataException( Error( source, "does not match the saved scene's identity" ) );
	}

	static void WriteAtomic( string path, byte[] data )
	{
		Directory.CreateDirectory( Path.GetDirectoryName( path ) );
		var temp = path + "." + Guid.NewGuid().ToString( "N" ) + ".tmp";
		try
		{
			File.WriteAllBytes( temp, data );
			File.Move( temp, path, overwrite: true );
		}
		finally
		{
			if ( File.Exists( temp ) )
				File.Delete( temp );
		}
	}

	static void RestoreMetadata( string source, byte[] previous, byte[] written, params string[] properties )
	{
		var current = ReadMetadataBytes( source );
		if ( current is null )
			return;

		if ( current.AsSpan().SequenceEqual( written ) )
		{
			if ( previous is null )
				File.Delete( MetadataPath( source ) );
			else
				WriteAtomic( MetadataPath( source ), previous );
			return;
		}

		var metadata = ReadMetadata( current );
		var updated = ReadMetadata( written );
		var original = ReadMetadata( previous );
		foreach ( var property in properties )
		{
			if ( metadata.ContainsKey( property ) != updated.ContainsKey( property )
				|| !JsonNode.DeepEquals( metadata[property], updated[property] ) )
				continue;

			if ( original.TryGetPropertyValue( property, out var value ) )
				metadata[property] = value?.DeepClone();
			else
				metadata.Remove( property );
		}
		WriteAtomic( MetadataPath( source ), JsonSerializer.SerializeToUtf8Bytes( metadata, JsonOptions ) );
	}

	static void RestoreFile( string path, byte[] previous )
	{
		if ( previous is not null )
			WriteAtomic( path, previous );
		else
			File.Delete( path );
	}

	internal static void ClearCompilation( Asset asset, Guid sceneId )
	{
		if ( !HasCompilation( asset ) )
			return;

		var source = SourcePath( asset );
		RequireSceneIdentity( source, sceneId );
		var manifest = ManifestPath( source );
		var compiled = CompiledPath( source );
		var previous = File.Exists( manifest ) ? File.ReadAllBytes( manifest ) : null;
		var previousCompiled = File.Exists( compiled ) ? File.ReadAllBytes( compiled ) : null;
		var previousMetadata = ReadMetadataBytes( source );
		var metadata = ReadMetadata( previousMetadata );
		metadata.Remove( RequiredProperty );
		var updatedMetadata = JsonSerializer.SerializeToUtf8Bytes( metadata, JsonOptions );
		var success = false;

		try
		{
			WriteAtomic( MetadataPath( source ), updatedMetadata );
			File.Delete( manifest );
			File.Delete( compiled );
			if ( !asset.Compile( true ) || asset.IsCompileFailed )
				throw new InvalidOperationException( $"Could not restore '{asset.Path}' to an ordinary runtime scene. The previous compilation has been preserved." );

			RequireSceneIdentity( source, sceneId );
			var runtime = ReadCompiledJson( source, out _ );
			if ( runtime?["__guid"]?.GetValue<Guid>() != sceneId || runtime["__scene_compiled"]?.GetValue<bool>() == true )
				throw new InvalidDataException( $"'{asset.Path}' still contains compiled scene data." );

			success = true;
		}
		finally
		{
			if ( !success )
			{
				try
				{
					RestoreFile( manifest, previous );
					RestoreFile( compiled, previousCompiled );
				}
				finally
				{
					RestoreMetadata( source, previousMetadata, updatedMetadata, RequiredProperty );
				}
			}
		}

		PruneGenerations( source, retired: true );
	}

	/// <summary>
	/// Publish a completed generation, then force the standard resource compiler to create .scene_c.
	/// Roll back the selector and compiled file if compilation fails; never touch .scene or .scene_d.
	/// </summary>
	internal static void Publish( Asset asset, string source, string generation, SceneFile file, SceneCompilerSettings settings, CancellationToken cancel )
	{
		settings.Validate();
		void RequireSource()
		{
			cancel.ThrowIfCancellationRequested();
			if ( asset.IsDeleted || !File.Exists( source )
				|| !string.Equals( source, asset.GetSourceFile( true ), StringComparison.OrdinalIgnoreCase ) )
				throw new InvalidDataException( Error( source, "was moved or deleted during compilation" ) );

			RequireSceneIdentity( source, file.Id );
		}

		RequireSource();

		var compilation = new Compilation { Version = Version, SceneId = file.Id, Generation = generation, Outputs = new() };
		var folder = Path.GetDirectoryName( OutputPath( source, compilation, SceneJson ) );
		file.IsCompiled = true;
		var jsonObject = file.Serialize();
		jsonObject["__scene_compiled"] = true;
		jsonObject[GenerationProperty] = generation;
		var json = jsonObject.ToJsonString( JsonOptions );
		WriteAtomic( OutputPath( source, compilation, SceneJson ), Encoding.UTF8.GetBytes( json ) );
		WriteAtomic( OutputPath( source, compilation, SceneBlob ), file.BinaryData ?? [] );

		foreach ( var output in Directory.EnumerateFiles( folder ) )
			compilation.Outputs.Add( Path.GetFileName( output ), OutputHash( output ) );

		var manifest = ManifestPath( source );
		var previous = File.Exists( manifest ) ? File.ReadAllBytes( manifest ) : null;
		var compiled = CompiledPath( source );
		var previousCompiled = File.Exists( compiled ) ? File.ReadAllBytes( compiled ) : null;
		var previousMetadata = ReadMetadataBytes( source );
		var metadata = ReadMetadata( previousMetadata );
		metadata[RequiredProperty] = true;
		metadata[SceneCompilerSettings.MetadataProperty] = JsonSerializer.SerializeToNode( settings );
		var updatedMetadata = JsonSerializer.SerializeToUtf8Bytes( metadata, JsonOptions );
		var metadataWritten = false;
		var success = false;

		try
		{
			RequireSource();
			if ( previousMetadata is null || !previousMetadata.AsSpan().SequenceEqual( updatedMetadata ) )
			{
				WriteAtomic( MetadataPath( source ), updatedMetadata );
				metadataWritten = true;
			}

			WriteAtomic( manifest, JsonSerializer.SerializeToUtf8Bytes( compilation, JsonOptions ) );
			if ( !asset.Compile( true ) || asset.IsCompileFailed || !File.Exists( compiled ) )
				throw new InvalidOperationException( $"Could not compile '{asset.Path}' into its runtime .scene_c. See the resource-compiler error in the editor console. The previous compilation has been preserved." );

			RequireSource();
			if ( !TryRead( source, out var published, out var error, requireCompiled: true ) )
				throw new InvalidDataException( error );
			if ( published?.Generation != generation )
				throw new InvalidDataException( Error( source, "does not select the completed generation" ) );
			success = true;
		}
		finally
		{
			if ( !success )
			{
				try
				{
					RestoreFile( manifest, previous );
					RestoreFile( compiled, previousCompiled );
				}
				finally
				{
					if ( metadataWritten )
						RestoreMetadata( source, previousMetadata, updatedMetadata, RequiredProperty, SceneCompilerSettings.MetadataProperty );
				}
			}
		}

		PruneGenerations( source );
	}
}
