using Sandbox.Internal;
using Sandbox.UI;
using Sandbox.Utility;

namespace Sandbox;

//
// We add objects like Components and GameObjectSystem to an object index, in which they're indexed for 
// fast lookup by type, basetypes and interfaces. This allows Scene.GetAll<T> to get interfaces etc very
// quickly compared to iterating individual items.
//
// Objects should be added when they're active and removed when they're inactive. For example, when a component
// or a component's GameObject is disabled, then it is removed from the object index. When it is enabled 
// again, it's re-added.
//

public partial class Scene : GameObject
{
	// objects collections indexed by type
	[SuppressNullKeyWarning]
	Dictionary<Type, HashSetEx<object>> objectIndex = new();

	// all objects in the index
	HashSet<object> objectsInIndex = new();

	// Component magic
	internal HashSetEx<Component> updateComponents = new();
	internal HashSetEx<Component> fixedUpdateComponents = new();
	internal HashSetEx<Component> preRenderComponents = new();

	// Per-type cache: maps a concrete type to the flat array of all indexable types
	// (the type itself + base types up to the stop types, plus all their interfaces).
	// Avoids repeated Type.GetInterfaces() allocations on every Add/Remove.
	// ReflectionCache is IHotloadManaged and clears itself automatically after a hotload.
	static readonly ReflectionCache<Type, Type[]> indexableTypesCache = new( rootType =>
	{
		var types = new List<Type>();
		var t = rootType;
		while ( t is not null )
		{
			if ( t == typeof( object ) ) break;
			if ( t == typeof( Component ) ) break;
			if ( t == typeof( GameObjectSystem ) ) break;
			if ( t == typeof( Panel ) ) break;
			if ( t == typeof( Label ) ) break;

			types.Add( t );
			types.AddRange( t.GetInterfaces() );

			t = t.BaseType;
		}
		return types.ToArray();
	} );

	/// <summary>
	/// Should only be called when destroying the scene. This here just to avoid unregistering
	/// all of the objects when we don't need to, because we're just quitting.
	/// </summary>
	void ClearObjectIndex()
	{
		foreach ( var t in objectIndex.Values )
		{
			t?.Clear();
		}

		objectsInIndex.Clear();
		objectIndex.Clear();
		updateComponents.Clear();
		fixedUpdateComponents.Clear();
		preRenderComponents.Clear();
	}

	/// <summary>
	/// When hotload occurs, the interfaces etc could have changed. So first of all we want to
	/// go through and remove any null entries, then we want to go through and re-add everything.
	/// </summary>
	void HotloadObjectIndex()
	{
		var ft = FastTimer.StartNew();

		// copy the list
		var temporaryHash = objectsInIndex.ToList();

		// clear the old list
		objectsInIndex.Clear();
		objectIndex.Clear();

		// We can't clear updateComponents etc because ActionGraph add to them manually

		// re-add everything
		foreach ( var obj in temporaryHash )
		{
			if ( obj is null ) continue;

			AddObjectToDirectory( obj );
		}

		if ( ft.ElapsedMilliSeconds > 10 )
		{
			Log.Warning( $"HotloadObjectIndex took {ft.ElapsedMilliSeconds:0.0}ms" );
		}
	}


	/// <summary>
	/// Adds object instance, indexed by type, to the directory so that its values are accessible by GetAll
	/// </summary>
	internal void AddObjectToDirectory( object obj )
	{
		if ( !objectsInIndex.Add( obj ) ) return;

		foreach ( var t in indexableTypesCache[obj.GetType()] )
		{
			objectIndex.GetOrCreate( t ).Add( obj );
		}

		// little bit of magic for components
		if ( obj is Component c )
		{
			if ( c is IUpdateSubscriber || c.OnComponentUpdate is not null ) updateComponents.Add( c );
			if ( c is IFixedUpdateSubscriber || c.OnComponentFixedUpdate is not null ) fixedUpdateComponents.Add( c );
			if ( c is IPreRenderSubscriber ) preRenderComponents.Add( c );
		}
	}

	/// <summary>
	/// Adds object instance, indexed by type, to the directory so that its values are accessible by GetAll
	/// </summary>
	internal void RemoveObjectFromDirectory( object obj )
	{
		if ( !objectsInIndex.Remove( obj ) ) return;

		foreach ( var t in indexableTypesCache[obj.GetType()] )
		{
			objectIndex.GetOrCreate( t ).Remove( obj );
		}

		// little bit of magic for components
		if ( obj is Component c )
		{
			if ( c is IUpdateSubscriber || c.OnComponentUpdate is not null ) updateComponents.Remove( c );
			if ( c is IFixedUpdateSubscriber || c.OnComponentFixedUpdate is not null ) fixedUpdateComponents.Remove( c );
			if ( c is IPreRenderSubscriber ) preRenderComponents.Remove( c );
		}
	}

	// The index set for a type, or null if nothing of that type is registered.
	internal HashSetEx<object> GetIndexSet( Type type )
		=> objectIndex.TryGetValue( type, out var set ) && set.Count > 0 ? set : null;

	/// <summary>
	/// Get all objects of this type. This could be a component or a GameObjectSystem, or other stuff in the future.
	/// </summary>
	/// <remarks>
	/// Allocates once per call. Engine code should prefer <see cref="GetAll{T}(List{T})"/>.
	/// </remarks>
	[Pure]
	public IEnumerable<T> GetAll<T>() => new SceneObjectEnumerable<T>( this );

	/// <summary>
	/// Get all objects of this type. This could be a component or a GameObjectSystem, or other stuff in the future.
	/// </summary>
	[Pure]
	public void GetAll<T>( List<T> target )
	{
		if ( !objectIndex.TryGetValue( typeof( T ), out var set ) || set.Count == 0 )
			return;

		foreach ( var e in set.EnumerateLocked() )
		{
			T c = (T)e;
			if ( c is null ) continue;
			if ( c is IValid v && !v.IsValid ) continue;

			target.Add( c );
		}
	}

	/// <summary>
	/// Gets the first object found of this type. This could be a component or a GameObjectSystem, or other stuff in the future.
	/// </summary>
	/// <typeparam name="T"></typeparam>
	/// <returns></returns>
	[Pure]
	public T Get<T>()
	{
		if ( !objectIndex.TryGetValue( typeof( T ), out var set ) || set.Count == 0 )
			return default;

		foreach ( var e in set.EnumerateLocked() )
		{
			T c = (T)e;
			if ( c is null ) continue;
			if ( c is IValid v && !v.IsValid ) continue;
			return c;
		}

		return default;
	}
}

/// <summary>
/// What <see cref="Scene.GetAll{T}()"/> returns. Hand written rather than a yield iterator, which costs the same
/// allocation but is ~25% slower to walk. Doubles as its own enumerator, so one foreach allocates once.
/// </summary>
sealed class SceneObjectEnumerable<T> : IEnumerable<T>, IEnumerator<T>
{
	private readonly Scene scene;
	private HashSetEx<object>.LockedEnumerator inner;
	private bool taken;
	private bool started;
	private bool holdsLock;

	public SceneObjectEnumerable( Scene scene )
	{
		this.scene = scene;
	}

	public IEnumerator<T> GetEnumerator()
	{
		if ( taken )
			return new SceneObjectEnumerable<T>( scene ).GetEnumerator();

		taken = true;
		return this;
	}

	System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

	public T Current { get; private set; }
	object System.Collections.IEnumerator.Current => Current;

	public bool MoveNext()
	{
		// Looked up here rather than in the constructor so the query stays lazy, like the iterator was.
		if ( !started )
		{
			started = true;

			var set = scene?.GetIndexSet( typeof( T ) );
			if ( set is null ) return false;

			inner = set.EnumerateLocked().GetEnumerator();
			holdsLock = true;
		}

		// Nothing was indexed, so there's no inner enumerator to step. Keeps returning false rather than throwing.
		if ( !holdsLock )
			return false;

		while ( inner.MoveNext() )
		{
			T c = (T)inner.Current;
			if ( c is null ) continue;
			if ( c is IValid v && !v.IsValid ) continue;

			Current = c;
			return true;
		}

		Current = default;
		return false;
	}

	// foreach disposes even if nothing was enumerated, and a default LockedEnumerator has no set to release.
	public void Dispose()
	{
		if ( holdsLock ) inner.Dispose();
	}

	public void Reset() => throw new NotSupportedException();
}
