﻿using NServiceBus;
using NUnit.Framework;
using Particular.Approvals;
using PublicApiGenerator;

[assembly: Ignore("CI testing")]

[TestFixture]
public class APIApprovals
{
    [Test]
    public void Approve()
    {
        var publicApi = typeof(SqlServerTransport).Assembly.GeneratePublicApi(new ApiGeneratorOptions
        {
            ExcludeAttributes = new[]
            {
                "System.Runtime.Versioning.TargetFrameworkAttribute",
                "System.Reflection.AssemblyMetadataAttribute"
            }
        });
        Approver.Verify(publicApi);
    }
}