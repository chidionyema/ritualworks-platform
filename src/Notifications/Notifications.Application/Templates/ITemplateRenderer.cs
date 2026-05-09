namespace Haworks.Notifications.Application.Templates;

public interface ITemplateRenderer
{
    Task<string> RenderAsync(string template, IDictionary<string, object> data);
}
