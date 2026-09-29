using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sandbox;

namespace Editor;

/// <summary>
/// Compiles a scene's static mesh geometry into the source asset's generated runtime representation.
/// The editable scene is never rewritten by this compiler.
/// </summary>
internal static partial class SceneCompiler
{
	/// <summary>
	/// Each run gets its own generation so failed or cancelled compilations leave the previous one intact.
	/// </summary>
	static string OutputFolder;

	/// <summary>
	/// The immutable recipe captured at the start of this compile. Window edits cannot change it.
	/// </summary>
	internal static SceneCompilerSettings Settings { get; private set; } = new();

	/// <summary>
	/// True while a compile is in flight. A compile is spread over frames and works out of shared
	/// state, so a second one starting on top of the first would trample it.
	/// </summary>
	static bool _running;

	/// <summary>
	/// Something in the scene we're leaving alone, and why. Kept as the component itself so the
	/// window can take you to it.
	/// </summary>
	internal readonly record struct Skip( Component Component, string Label, string Reason );

	/// <summary>
	/// What a compile is going to work on, worked out up front so it can be shown before anything
	/// is built.
	/// </summary>
	internal sealed class Sources
	{
		public Scene Scene { get; init; }
		public Asset Asset { get; init; }
		public SceneFolder Folder { get; init; }
		public string Name { get; init; }
		public MeshComponent[] Meshes { get; init; }
		public ModelRenderer[] Props { get; init; }
		public List<Skip> Skipped { get; init; }
		public bool HasCompileGeometry { get; init; }
	}

	[Menu( "Editor", "Scene/View Compile Report", "list", Priority = 1002 )]
	public static void ViewCompileReport()
	{
		var session = SceneCompileSession.Current;
		session.Refresh();
		EditorEvent.Run( "scene.compile.show-report", session.HasResult ? session.CreateReportSnapshot() : session, "Report" );
	}

	/// <summary>
	/// Everything the active scene has to compile, and why the rest is being left alone. Null with a
	/// reason when the scene can't be compiled at all.
	/// </summary>
	internal static Sources Scan( Scene scene, out string error )
	{
		error = null;

		if ( !scene.IsValid() )
		{
			error = "No scene is open.";
			return null;
		}

		var sources = DiscoverSources( scene ).ToArray();
		var hasCompileGeometry = sources.Any( x => x.NeedsCompilation );
		if ( hasCompileGeometry && (scene.Editor is null || scene.Editor.HasUnsavedChanges) )
		{
			error = "Save the scene, then use Scene > Compile Scene. Unsaved changes cannot be compiled.";
			return null;
		}

		var asset = scene.Source is null ? null : AssetSystem.FindByPath( scene.Source.ResourcePath );
		if ( hasCompileGeometry && asset is null )
		{
			error = "Save the scene before compiling it.";
			return null;
		}

		var folder = asset is not null && scene.Editor?.HasUnsavedChanges == false ? scene.Editor.GetSceneFolder() : null;
		if ( hasCompileGeometry && folder is null )
		{
			error = "This scene has nowhere to write its compiled resources.";
			return null;
		}

		var skipped = new List<Skip>();

		return new Sources
		{
			Scene = scene,
			Asset = asset,
			Folder = folder,
			Name = asset is null ? scene.Name : Path.GetFileNameWithoutExtension( asset.AbsolutePath ),
			Meshes = Gather<MeshComponent>( sources, skipped ),
			Props = Gather<ModelRenderer>( sources, skipped ),
			Skipped = skipped,
			HasCompileGeometry = hasCompileGeometry,
		};
	}

	/// <summary>
	/// Compile geometry in the editor, yielding between steps while preserving native thread affinity.
	/// </summary>
	internal static async Task<(string[] Summary, bool IsCurrent)> Compile( Sources sources, SceneCompilerSettings settings, SceneCompileSession session )
	{
		if ( _running )
			throw new InvalidOperationException( "A scene compile is already running." );

		ArgumentNullException.ThrowIfNull( settings );
		settings.Validate();
		Settings = settings;
		var generation = Guid.NewGuid().ToString( "N" );
		var sourcePath = sources.Asset.GetSourceFile( true );
		_running = true;
		OutputFolder = $"/compiled/{generation}";
		(string[] Summary, bool IsCurrent) result = default;
		Scene compiled = null;

		try
		{
			var scene = sources.Scene;
			if ( !scene.IsValid() || Game.IsPlaying || scene.Editor is SceneEditorSession { IsPrefabSession: true } )
				throw new InvalidOperationException( "Stop playing and open a scene rather than a prefab before compiling." );

			if ( scene.Editor is null || scene.Editor.HasUnsavedChanges )
				throw new InvalidOperationException( "Save the scene, then use Scene > Compile Scene. Unsaved changes cannot be compiled." );

			session.Cancel.ThrowIfCancellationRequested();
			session.Phase( "Copying scene" );
			var snapshot = SceneCompileCache.Capture( sources.Asset );
			var sourceFile = scene.CreateSceneFile();

			// A game scene would also load the project's system scene and network spawns.
			compiled = Scene.CreateEditorScene();
			using ( compiled.Push() )
			{
				sourceFile.ActionGraphCache.Clear();
				if ( !compiled.Load( sourceFile ) )
					throw new InvalidOperationException( "Could not load the editable scene for compilation." );
			}

			SceneCompileCache.BeginGeneration( sources.Asset, generation );
			result = await Run( sources, compiled, sourceFile.Id, sourcePath, snapshot, session, generation );
		}
		finally
		{
			try
			{
				compiled?.Destroy();
			}
			finally
			{
				try
				{
					if ( result.Summary is null )
						SceneCompileCache.DiscardGeneration( sourcePath, generation );
				}
				finally
				{
					_running = false;
					OutputFolder = null;
				}
			}
		}

		return result;
	}

	static async Task<(string[] Summary, bool IsCurrent)> Run( Sources sources, Scene compiled, Guid sceneId, string sourcePath,
		SceneCompileCache.Snapshot snapshot, SceneCompileSession session, string generation )
	{
		var sourceAsset = sources.Asset;
		var sceneFolder = sources.Folder;
		var discovered = DiscoverSources( compiled ).ToArray();
		var meshes = Gather<MeshComponent>( discovered );
		var props = Gather<ModelRenderer>( discovered );

		var frame = FastTimer.StartNew();

		// Pumping the editor costs more than most of the work between two steps, so we only do it
		// on a frame's cadence rather than for every item.
		async Task Step( int current, int total )
		{
			session.Cancel.ThrowIfCancellationRequested();

			if ( frame.ElapsedMilliSeconds < 30 )
				return;

			session.Step( current, total );

			await Task.Delay( 1, session.Cancel );

			frame = FastTimer.StartNew();
		}

		session.Phase( "Compiling geometry" );
		await Task.Delay( 1, session.Cancel );

		var processed = new HashSet<Guid>();
		var plan = await Plan( meshes, props, processed, Step, session.Cancel );

		if ( plan is null )
			return default;

		var plans = plan.Aggregates;
		var statistics = new SceneCompileStatistics();

		session.Phase( "Building models" );

		var fragments = new AggregateFragmentInfo[plans.Length][];
		var vmdls = new byte[plans.Length][];

		for ( int i = 0; i < plans.Length; i++ )
		{
			var build = Build( plans[i] );

			fragments[i] = build.Fragments;
			vmdls[i] = build.Model.SaveToVmdl();
			if ( !plans[i].Translucent )
				statistics.FragmentCount += build.Fragments.Length;

			foreach ( var chunk in plans[i].Chunks )
			{
				statistics.VertexCount += chunk.Vertices.Length;
				statistics.TriangleCount += chunk.Indices.Length / 3;
			}

			await Step( i + 1, plans.Length );
		}

		session.Phase( "Building collision" );
		await Task.Delay( 1, session.Cancel );

		var physics = await BuildCollision( plan.Collision, plan.Shapes, Step );

		session.Phase( "Writing resources" );
		await Task.Delay( 1, session.Cancel );

		session.Cancel.ThrowIfCancellationRequested();

		var models = new Model[plans.Length];

		for ( int i = 0; i < plans.Length; i++ )
		{
			models[i] = Model.Load( Write( sceneFolder, $"{OutputFolder}/aggregate_{i}.vmdl_c", vmdls[i] ) );
			if ( !models[i].IsValid() || models[i].IsError )
				throw new InvalidOperationException( $"Could not load compiled aggregate model {i}." );

			await Step( i + 1, plans.Length );
		}

		var collision = new PhysicsGroupDescription[physics.Count];

		for ( int i = 0; i < physics.Count; i++ )
		{
			collision[i] = PhysicsGroupDescription.Load( Write( sceneFolder, $"{OutputFolder}/collision_{i}.vphys_c", physics[i].Data ) );
			if ( collision[i] is null )
				throw new InvalidOperationException( $"Could not load compiled collision resource {i}." );

			await Step( i + 1, physics.Count );
		}

		var converted = 0;
		SceneFile file = null;

		var leftovers = new HashSet<Guid>();

		foreach ( var mesh in compiled.Components.GetAll<MeshComponent>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( !processed.Contains( mesh.Id ) )
			{
				leftovers.Add( mesh.Id );
			}
		}

		if ( leftovers.Count > 0 )
		{
			session.Phase( $"Converting {leftovers.Count} meshes" );
			await Task.Delay( 1, session.Cancel );

			converted = await ConvertMeshes( compiled, leftovers, sceneFolder, statistics, Step );
			processed.UnionWith( leftovers );
		}

		session.Phase( "Stripping compiled geometry" );
		await Task.Delay( 1, session.Cancel );

		using ( compiled.Push() )
		{
			// Unlink affected prefabs before stripping their source
			// components so those components cannot return when the prefab expands again.
			foreach ( var go in compiled.Children.ToArray() )
			{
				Unlink( go, processed );
			}

			StripCompiled( compiled, processed );

			session.Phase( "Building objects" );

			GameObject root = null;

			// Nothing under here is meant to be touched by hand - the next compile throws it all
			// away and builds it again, so keep it out of the hierarchy and out of selection.
			if ( plans.Length > 0 || collision.Length > 0 )
			{
				root = compiled.CreateObject();
				root.Name = "World";
				root.IsStatic = true;
				root.Flags |= GameObjectFlags.Hidden;
			}

			for ( int i = 0; i < plans.Length; i++ )
			{
				var go = compiled.CreateObject();
				go.SetParent( root );
				go.Flags |= GameObjectFlags.Hidden;
				ApplyTags( go, plans[i].Tags );

				// Aggregates are an opaque path, so translucent geometry is compiled into a model
				// and drawn like any other model instead.
				if ( plans[i].Translucent )
				{
					go.Name = $"Translucent {i}";
					go.LocalTransform = plans[i].Transform;

					var model = go.AddComponent<ModelRenderer>();
					model.Model = models[i];
					model.Tint = plans[i].Tint;

					continue;
				}

				go.Name = $"Aggregate {i}";

				var renderer = go.AddComponent<AggregateRenderer>();
				renderer.Model = models[i];
				renderer.Tint = plans[i].Tint;
				renderer.Fragments = fragments[i].ToList();
			}

			for ( int i = 0; i < collision.Length; i++ )
			{
				var go = compiled.CreateObject();
				go.Name = $"Collision {i}";
				go.SetParent( root );
				go.Flags |= GameObjectFlags.Hidden;
				ApplyTags( go, physics[i].Tags );

				var collider = go.AddComponent<PhysicsCollider>();
				collider.Physics = collision[i];
				collider.Static = true;
			}

			if ( compiled.Components.GetAll<MeshComponent>( FindMode.EverythingInSelfAndDescendants ).FirstOrDefault() is { } remainingMesh )
				throw new InvalidOperationException( $"Cannot publish the compiled scene: mesh '{remainingMesh.GameObject.Name}' was not converted. Compiled scenes cannot contain MeshComponents." );

			file = new SceneFile();
			compiled.ToSceneFile( file );
			file.Id = sceneId;
		}

		session.Phase( "Writing runtime scene" );
		var isCurrent = SceneCompileCache.Publish( sourceAsset, sourcePath, generation, file, snapshot, Settings, session.Cancel );
		Settings.SaveDefaults();
		session.Statistics = statistics;

		var translucent = plans.Count( x => x.Translucent );
		var aggregateCount = plans.Length - translucent;
		var summary = new List<string> { $"{aggregateCount:n0} {(aggregateCount == 1 ? "aggregate" : "aggregates")}" };

		if ( translucent > 0 ) summary.Add( $"{translucent:n0} translucent {(translucent == 1 ? "model" : "models")}" );
		if ( converted > 0 ) summary.Add( $"{converted:n0} converted {(converted == 1 ? "mesh" : "meshes")}" );
		if ( collision.Length > 0 ) summary.Add( $"{collision.Length:n0} collision {(collision.Length == 1 ? "group" : "groups")}" );

		return ([.. summary], isCurrent);
	}

	/// <summary>
	/// Write a generated resource into this run's private generation.
	/// </summary>
	static string Write( SceneFolder folder, string path, byte[] data )
	{
		var written = folder.WriteFile( path, data );

		// The resource system wants the source name, not the compiled one.
		var name = written.EndsWith( "_c" ) ? written[..^2] : written;

		NativeEngine.g_pResourceSystem.ReloadResource( name );

		return written;
	}

	/// <summary>
	/// Everything in the scene we can compile, noting what we're leaving alone and why.
	/// </summary>
	static T[] Gather<T>( IEnumerable<Source> sources, List<Skip> skipped = null ) where T : Component
	{
		var found = new List<T>();

		foreach ( var source in sources )
		{
			if ( source.Component is not T component || !component.Active )
				continue;

			if ( source.SkipReason is not { } reason )
			{
				found.Add( component );
				continue;
			}

			skipped?.Add( new Skip( component, source.Label, reason ) );
		}

		return [.. found];
	}

	/// <summary>
	/// Break every prefab instance holding compiled geometry, so the components we're about to strip
	/// stay stripped. Breaking an instance promotes the ones nested in it, so we go back through
	/// its children once it's loose.
	/// </summary>
	static bool Unlink( GameObject go, HashSet<Guid> processed )
	{
		var holds = Holds( go, processed );

		foreach ( var child in go.Children.ToArray() )
		{
			holds |= Unlink( child, processed );
		}

		if ( !holds || !go.IsOutermostPrefabInstanceRoot )
			return holds;

		go.BreakFromPrefab();

		foreach ( var child in go.Children.ToArray() )
		{
			Unlink( child, processed );
		}

		return true;
	}

	static bool Holds( GameObject go, HashSet<Guid> processed )
	{
		foreach ( var component in go.Components.GetAll( FindMode.EverythingInSelf ) )
		{
			if ( processed.Contains( component.Id ) )
				return true;
		}

		return false;
	}

	/// <summary>
	/// Put back the tags the compiled geometry inherited before it left its old parents.
	/// </summary>
	static void ApplyTags( GameObject go, string tags )
	{
		if ( string.IsNullOrEmpty( tags ) )
			return;

		go.Tags.Add( tags.Split( ',' ) );
	}

	/// <summary>
	/// Remove everything we compiled from the hierarchy, taking an object with it when that was all it
	/// had. Children go first, so an object left holding nothing after its compiled children left goes
	/// too. Components are matched by id, which survives the round trip through the scene file.
	/// Returns whether anything under here was removed.
	/// </summary>
	static bool StripCompiled( GameObject go, HashSet<Guid> processed )
	{
		var stripped = false;

		foreach ( var child in go.Children.ToArray() )
		{
			stripped |= StripCompiled( child, processed );
		}

		foreach ( var component in go.Components.GetAll( FindMode.EverythingInSelf ).ToArray() )
		{
			if ( !processed.Contains( component.Id ) )
				continue;

			component.Destroy();
			stripped = true;
		}

		// Objects that were already empty are the author's, so only clear up after ourselves
		if ( stripped && go is not Scene && go.Components.Count == 0 && go.Children.Count == 0 )
			go.DestroyImmediate();

		return stripped;
	}
}
