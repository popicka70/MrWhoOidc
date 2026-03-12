sed -i 's/TokenHandler(/TokenHandler(services.GetRequiredService<IAuditSink>(), /g' ./MrWhoOidc.UnitTests/TokenHandlerTests.cs
