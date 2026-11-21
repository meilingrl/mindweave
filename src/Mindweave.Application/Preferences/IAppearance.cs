namespace Mindweave.Application.Preferences;

/// <summary>The appearance preference as the settings page uses it (course requirement C3).</summary>
public interface IAppearance
{
    Task<Appearance> GetAsync(CancellationToken cancellationToken);

    Task SetAsync(Appearance appearance, CancellationToken cancellationToken);

    Task<BoldColour> GetBoldColourAsync(CancellationToken cancellationToken);

    Task SetBoldColourAsync(BoldColour colour, CancellationToken cancellationToken);
}
