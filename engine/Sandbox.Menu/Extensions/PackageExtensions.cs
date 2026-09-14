using System;

namespace Sandbox;

public static partial class SandboxMenuExtensions
{
	/// <summary>
	/// Mark this package as a favourite
	/// </summary>
	public static async Task SetFavouriteAsync( this Package package, bool state )
	{
		try
		{
			var f = await Sandbox.Backend.Package.SetFavourite( package.FullIdent, state );
			if ( !f.Success ) return;

			var i = package.Interaction;
			i.Favourite = state;
			i.FavouriteCreated = DateTime.UtcNow;
			package.Interaction = i;

			package.Favourited = f.Total;

			// Remove from our faves
			AccountInformation.Favourites.RemoveAll( x => x.FullIdent == package.FullIdent );

			// If it's a favourite, add it to our list
			if ( state && package is RemotePackage rp )
			{
				AccountInformation.Favourites.Add( rp );
			}
		}
		catch ( Refit.ApiException e )
		{
			Log.Warning( $"Couldn't set favourite {package.FullIdent} ({e.Message})" );
		}
	}

	/// <summary>
	/// Hide this package from the local player's discovery and search. False if the backend refused.
	/// </summary>
	public static async Task<bool> SetHiddenAsync( this Package package, bool hidden )
	{
		try
		{
			await Sandbox.Backend.Account.SetPackageHidden( package.FullIdent, hidden );
			return true;
		}
		catch ( Exception e )
		{
			Log.Warning( $"Couldn't hide {package.FullIdent} ({e.Message})" );
			return false;
		}
	}

	/// <summary>
	/// Add your vote for this package
	/// </summary>
	public static async Task SetVoteAsync( this Package package, bool up )
	{
		var value = up ? 0 : 1;

		// already is this
		if ( package.Interaction.Rating.HasValue && package.Interaction.Rating.Value == value )
			return;

		try
		{
			var f = await Sandbox.Backend.Package.SetRating( package.FullIdent, value );
			if ( !f.Success ) return;

			var i = package.Interaction;
			i.Rating = up ? 0 : 1;
			i.RatingCreated = DateTime.UtcNow;
			package.Interaction = i;

			package.VotesUp = f.VotesUp;
			package.VotesDown = f.VotesDown;
		}
		catch ( Refit.ApiException e )
		{
			Log.Warning( $"Couldn't rate {package.FullIdent} ({e.Message})" );
		}
	}

	/// <summary>
	/// Open a modal for the specific package. This will open the correct modal
	/// </summary>
	public static void OpenModal( this Package package )
	{
		if ( package.TypeName == "game" )
		{
			Game.Overlay.ShowGameModal( package.FullIdent );
			return;
		}

		if ( package.TypeName == "map" )
		{
			Game.Overlay.ShowMapModal( package.FullIdent );
			return;
		}

		Game.Overlay.ShowPackageModal( package.FullIdent );
	}
}
