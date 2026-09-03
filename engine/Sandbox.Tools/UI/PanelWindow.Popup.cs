using NativeEngine;
using Sandbox.Engine.Settings;
using Sandbox.UI;
using System;

namespace Editor;

public sealed partial class PanelWindow
{
	bool _isPopup;
	PanelWindow _parent;
	Vector2 _pendingPosition;
	Vector2 _pendingWindowSize;

	bool IPanelWindow.IsPopup => _isPopup;

	bool _ignoresInput;

	bool IPanelWindow.IgnoresInput => _ignoresInput;

	/// <summary>
	/// A popup that never takes the keyboard or the mouse - a tooltip, say. It can't be focused,
	/// and the mouse goes to whatever is under it.
	/// </summary>
	public bool IgnoresInput => _ignoresInput;

	/// <summary>
	/// Open a popup - a borderless window that sits above its parent and can hang outside it, the
	/// way an OS menu does. The position is in the parent's client pixels, which is what a panel's
	/// <c>Box.Rect</c> is already in.
	/// <para>
	/// There is no size to pass. The popup is born hidden at the parent's size, shrinks to whatever
	/// is put in it, and only then appears - so what it ends up as is the size of its contents.
	/// </para>
	/// </summary>
	public static PanelWindow Popup( PanelWindow parent, Vector2 position, bool ignoresInput = false )
	{
		ArgumentNullException.ThrowIfNull( parent );

		// SDL popup windows position themselves relative to their parent, in window coordinates
		return new PanelWindow( parent, parent.PixelsToWindow( position ), ignoresInput );
	}

	PanelWindow( PanelWindow parent, Vector2 localPosition, bool ignoresInput )
	{
		ThreadSafe.AssertIsMainThread();

		_isPopup = true;
		_ignoresInput = ignoresInput;
		_shown = false;
		SizeToContents = true;
		Borderless = true;

		_parent = parent;
		_pendingPosition = localPosition;

		// Start as big as the parent and let FitToContents take it down. Starting small would make
		// the contents lay out against a width they're about to lose, and it's that first layout
		// FitToContents measures.
		_pendingWindowSize = parent.PixelsToWindow( parent.PixelSize );

		Surface = new UISurface { DpiScale = parent.Surface.DpiScale, Size = parent.PixelSize };
		Surface.OnCursorChanged = x => _cursor = x;
		Surface.Tooltips.Host = this;

		// The OS rounds and clips this window like its own menus - the styles square off
		// what would double-round inside that clip
		Surface.Root.AddClass( "os-popup" );

		// The root starts as big as the parent, and a stretched child would report the whole of
		// that back as its size and never shrink. Set here rather than in a stylesheet because
		// the root is above whatever sheet the contents bring with them.
		Surface.Root.Style.AlignItems = Align.FlexStart;

		_all.Add( this );
		PanelWindows.Register( this );
	}

	/// <summary>
	/// Make the OS window. Popups wait until the frame boundary for this - building a swap chain
	/// while another window is mid-render leaves it in a state that never presents.
	/// </summary>
	void CreateNativeWindow()
	{
		var x = (int)_pendingPosition.x;
		var y = (int)_pendingPosition.y;
		var width = (int)MathF.Ceiling( _pendingWindowSize.x );
		var height = (int)MathF.Ceiling( _pendingWindowSize.y );

		//
		// A real SDL popup window - positioned relative to its parent, kept above it, hidden and
		// destroyed along with it. The menu flag takes keyboard focus, so a click anywhere else
		// pulls focus away and the owner can dismiss it.
		//
		// Hidden until the first frame has been drawn - a popup born visible flashes a blank
		// window at its starting size before the UI sizes and fills it.
		//
		var flags = SdlWindowFlags.PopupMenu | SdlWindowFlags.Vulkan | SdlWindowFlags.HighPixelDensity | SdlWindowFlags.Hidden;

		// A window that ignores input never takes keyboard focus either - so a tooltip appearing
		// doesn't pull the caret out of a text entry. Not SDL_WINDOW_TOOLTIP: a swap chain on one
		// of those never presents. A menu popup flagged not focusable is what SDL documents for
		// this anyway.
		if ( _ignoresInput )
			flags |= SdlWindowFlags.NotFocusable;

		_window = EngineGlobal.SDL_CreatePopupWindow( _parent._window, x, y, width, height, (ulong)flags );

		if ( _window == IntPtr.Zero )
			throw new Exception( $"Couldn't create the popup: {EngineGlobal.SDL_GetError()}" );

		// The compositor rounds the corners, the way it rounds the OS's own menus. Drawing our
		// own rounding needs a transparent swapchain, and Windows doesn't composite Vulkan alpha
		// - the corner pixels come out as uninitialized garbage.
		EngineGlobal.SDL_SetWindowRoundedCorners( _window, true );

		// The mouse falls straight through to the window underneath, which keeps its hover
		if ( _ignoresInput )
			EngineGlobal.SDL_SetWindowMouseTransparent( _window, true );

		PanelWindowNative.Setup( _window );

		// A popup can open on a display that scales differently to the window that spawned it
		Surface.DpiScale = PanelWindowNative.GetContentsScale( _window );

		_swapChain = PanelWindowNative.CreateSwapChain( _window, (int)RenderSettings.Instance.AntiAliasQuality.ToEngine(), false );
		_swapChainSize = PixelSize;

		_world = new SceneWorld();

		_camera = new SceneCamera( "PanelWindow Popup" )
		{
			World = _world,

			// Opaque - the compositor rounds the window's corners itself. Windows doesn't
			// composite swapchain alpha, so drawing our own round corners isn't an option.
			BackgroundColor = Color.Black,
			ClearFlags = ClearFlags.All,
			EnablePostProcessing = false,
			ZNear = 1,
			ZFar = 1000,
		};
	}
}

/// <summary>
/// SDL_WindowFlags, the ones a popup needs. Values are SDL3's.
/// </summary>
[Flags]
enum SdlWindowFlags : ulong
{
	Hidden = 0x8,
	HighPixelDensity = 0x2000,
	PopupMenu = 0x80000,
	Vulkan = 0x10000000,
	NotFocusable = 0x80000000,
}
