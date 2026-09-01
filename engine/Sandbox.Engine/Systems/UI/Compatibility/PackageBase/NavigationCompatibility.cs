using System.ComponentModel;
using Sandbox.UI.Navigation;

namespace Sandbox.UI;

/// <summary>Pre-2025 name for NavigationHost, retained for package.base source compatibility.</summary>
[Hide, EditorBrowsable( EditorBrowsableState.Never )]
public class NavHostPanel : NavigationHost
{
}

/// <summary>Pre-2025 name for Navigation.NavLinkPanel.</summary>
[Hide, EditorBrowsable( EditorBrowsableState.Never )]
public class NavLinkPanel : Navigation.NavLinkPanel
{
}

/// <summary>Pre-2025 navigation lifecycle interface.</summary>
[Hide, EditorBrowsable( EditorBrowsableState.Never )]
public interface INavigatorPage : Navigation.INavigatorPage
{
}

/// <summary>Pre-2025 navigation extension methods.</summary>
[Hide, EditorBrowsable( EditorBrowsableState.Never )]
public static class NavigationExtensions
{
	public static void Navigate( this Panel panel, string url )
		=> panel.AncestorsAndSelf.OfType<NavigationHost>().FirstOrDefault()?.Navigate( url );
}
