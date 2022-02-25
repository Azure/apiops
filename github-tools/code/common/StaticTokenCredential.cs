namespace common;

public class StaticTokenCredential : TokenCredential
{
    private readonly AccessToken token;

    public StaticTokenCredential(string token)
    {
        this.token = new AccessToken(token, DateTimeOffset.Now.AddHours(1)); ;
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        return token;
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(token);
    }
}
