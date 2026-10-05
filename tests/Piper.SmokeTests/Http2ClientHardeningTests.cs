using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Http2;
using Piper.Core.Http2.Hpack;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// The client role of HTTP/2 (Http2ClientConnection) against a hand-scripted origin, so every frame
// a hostile or unlucky origin can send is sent exactly as the test wants it. The scripted origin is
// a bare loopback socket: no TLS, no Piper server code, so a symmetric bug in the two roles cannot
// hide. Each test that used to hang or run out of memory must now fail fast with a named reason.
internal static class Http2ClientHardeningTests
{
    // How long a test waits for the client before it counts a hang as a failure.
    private static readonly TimeSpan HangLimit = TimeSpan.FromSeconds(6);

    public static async Task RunAsync(TestRunner runner)
    {
        await RunHeaderBlockTestsAsync(runner);
        await RunFrameSequenceTestsAsync(runner);
        await RunFlowControlTestsAsync(runner);
        await RunGoAwayTestsAsync(runner);
        await RunIdleTimeoutTestsAsync(runner);
        await RunProxyTimeoutTestAsync(runner);
    }

    // ------------------------------------------------------------------ header blocks

    private static async Task RunHeaderBlockTestsAsync(TestRunner runner)
    {
        await runner.RunAsync("h2 client: an endless CONTINUATION run is cut at the header cap with GOAWAY", async () =>
        {
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                // No END_HEADERS anywhere: an origin that never finishes the block. 200 x 16 KiB is
                // 3 MiB, far past the 64 KiB the client advertised; it must not be read to the end.
                await p.SendAsync(Http2FrameType.Headers, Http2FrameFlags.None, 1, new byte[16_384]);
                for (var i = 0; i < 200; i++)
                    await p.SendAsync(Http2FrameType.Continuation, Http2FrameFlags.None, 1, new byte[16_384]);
            });

            var failure = await FailureOfAsync(peer, null, Get());

            runner.IsTrue(failure is Http2ProtocolException { ErrorCode: Http2ErrorCode.EnhanceYourCalm },
                $"the flood is refused as ENHANCE_YOUR_CALM (was: {Describe(failure)})");
            runner.AreEqual(Http2ErrorCode.EnhanceYourCalm, await peer.GoAwayCodeAsync(), "and the origin is told with GOAWAY");
        });

        await runner.RunAsync("h2 client: endless empty CONTINUATION frames are refused, with or without an idle timeout", async () =>
        {
            // Nothing is ever added to the block, so the byte cap cannot trip: the number of frames has to.
            foreach (var idle in new TimeSpan?[] { null, TimeSpan.FromMilliseconds(400) })
            {
                await using var peer = new Peer(async p =>
                {
                    await p.WaitForRequestHeadAsync();
                    await p.SendAsync(Http2FrameType.Headers, Http2FrameFlags.None, 1, ReadOnlyMemory<byte>.Empty);
                    while (true)
                        await p.SendAsync(Http2FrameType.Continuation, Http2FrameFlags.None, 1, ReadOnlyMemory<byte>.Empty);
                });

                var failure = await FailureOfAsync(peer, idle, Get());

                runner.IsTrue(failure is Http2ProtocolException { ErrorCode: Http2ErrorCode.EnhanceYourCalm },
                    $"idle timeout {(idle is null ? "off" : "on")}: refused as ENHANCE_YOUR_CALM (was: {Describe(failure)})");
            }
        });

        await runner.RunAsync("h2 client: a block of many tiny fragments is refused under the byte cap, and 64 fragments are fine", async () =>
        {
            await using var tiny = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendAsync(Http2FrameType.Headers, Http2FrameFlags.None, 1, new byte[1]);
                for (var i = 0; i < 300; i++)
                    await p.SendAsync(Http2FrameType.Continuation, Http2FrameFlags.None, 1, new byte[1]);
            });
            var failure = await FailureOfAsync(tiny, null, Get());
            runner.IsTrue(failure is Http2ProtocolException { ErrorCode: Http2ErrorCode.EnhanceYourCalm },
                $"300 one-byte fragments (301 bytes in all) are refused (was: {Describe(failure)})");

            var block = HpackEncoder.Encode([(":status", "200"), ("x-pad", new string('p', 200))]);
            await using var exact = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendHeaderBlockAsync(block, Http2FrameFlags.EndStream, chunkSize: (block.Length + 63) / 64);
            });
            var connection = new Http2ClientConnection(await exact.ConnectAsync());
            var head = await connection.SendRequestHeadAsync(Get(), exact.Ct).WaitAsync(HangLimit);
            runner.AreEqual(200, head.StatusCode, "a block split into 64 fragments is still accepted");
        });

        await runner.RunAsync("h2 client: a small block that expands to a huge header list is refused", async () =>
        {
            // One 4 KiB dynamic-table entry, then 60,000 one-byte references to it: a 64 KB block that
            // stands for about 240 MB of header text.
            var block = new List<byte> { 0x40, 0x01, (byte)'x' };
            block.AddRange(HpackLength(4000, prefixBits: 7));
            block.AddRange(Enumerable.Repeat((byte)'a', 4000));
            block.AddRange(Enumerable.Repeat((byte)0xBE, 60_000));
            runner.IsTrue(block.Count < 65_536, "the block itself is under the fragment cap, so only the decoded size can stop it");

            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendHeaderBlockAsync(block.ToArray(), Http2FrameFlags.None);
            });

            var failure = await FailureOfAsync(peer, null, Get());

            runner.IsTrue(failure is Http2ProtocolException { ErrorCode: Http2ErrorCode.EnhanceYourCalm },
                $"the expanded list is refused (was: {Describe(failure)})");
        });

        await runner.RunAsync("h2 client: a large header block split over CONTINUATION frames still works", async () =>
        {
            var fields = new List<(string, string)> { (":status", "200"), ("x-big", new string('v', 40_000)) };
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendHeaderBlockAsync(HpackEncoder.Encode(fields), Http2FrameFlags.None);
                await p.SendAsync(Http2FrameType.Data, Http2FrameFlags.EndStream, 1, "ok"u8.ToArray());
            });

            var connection = new Http2ClientConnection(await peer.ConnectAsync());
            var head = await connection.SendRequestHeadAsync(Get(), peer.Ct).WaitAsync(HangLimit);

            runner.AreEqual(200, head.StatusCode, "status");
            runner.AreEqual(40_000, head.Headers["x-big"]?.Length ?? 0, "all 40,000 header bytes arrived across the fragments");
            runner.AreEqual("ok", await ReadAllAsync(connection.ResponseBody), "body");
        });

        await runner.RunAsync("h2 client: a few 1xx responses before the final one are fine, endless ones are not", async () =>
        {
            var interim = HpackEncoder.Encode([(":status", "103"), ("link", "</a.css>; rel=preload")]);
            var final = HpackEncoder.Encode([(":status", "200")]);

            await using var few = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                for (var i = 0; i < 3; i++) await p.SendHeaderBlockAsync(interim, Http2FrameFlags.None);
                await p.SendHeaderBlockAsync(final, Http2FrameFlags.EndStream);
            });
            var connection = new Http2ClientConnection(await few.ConnectAsync());
            var head = await connection.SendRequestHeadAsync(Get(), few.Ct).WaitAsync(HangLimit);
            runner.AreEqual(200, head.StatusCode, "three interim responses, then the real one");

            await using var many = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                for (var i = 0; i < 1_000; i++) await p.SendHeaderBlockAsync(interim, Http2FrameFlags.None);
            });
            var failure = await FailureOfAsync(many, null, Get());
            runner.IsTrue(failure is Http2ProtocolException, $"an endless run of 1xx responses is refused (was: {Describe(failure)})");
        });

        await runner.RunAsync("h2 client: a :status that is not three digits in 100-599 is refused, not read as an interim response", async () =>
        {
            // "0150" was read (culture-sensitively) as 150 and skipped as an interim response; the shared
            // parser says it is not a status at all.
            foreach (var badStatus in new[] { "0150", "+150", "0", "99", "600", "999", "20x", "" })
            {
                await using var peer = new Peer(async p =>
                {
                    await p.WaitForRequestHeadAsync();
                    await p.SendHeaderBlockAsync(HpackEncoder.Encode([(":status", badStatus)]), Http2FrameFlags.None);
                    await p.SendHeaderBlockAsync(HpackEncoder.Encode([(":status", "200")]), Http2FrameFlags.EndStream);
                });
                var failure = await FailureOfAsync(peer, null, Get());
                runner.IsTrue(failure is HttpParseException or Http2ProtocolException,
                    $":status '{badStatus}' is refused (was: {Describe(failure)})");
            }
        });
    }

    // ------------------------------------------------------------------ frame sequencing

    private static async Task RunFrameSequenceTestsAsync(TestRunner runner)
    {
        var okBlock = HpackEncoder.Encode([(":status", "200")]);

        var cases = new (string Name, Func<Peer, Task> Script)[]
        {
            ("another frame in the middle of a header block", async p =>
            {
                await p.SendAsync(Http2FrameType.Headers, Http2FrameFlags.None, 1, okBlock);
                await p.SendAsync(Http2FrameType.Ping, Http2FrameFlags.None, 0, new byte[8]);
            }),
            ("CONTINUATION with no HEADERS before it", p =>
                p.SendAsync(Http2FrameType.Continuation, Http2FrameFlags.EndHeaders, 1, okBlock)),
            ("CONTINUATION for a different stream", async p =>
            {
                await p.SendAsync(Http2FrameType.Headers, Http2FrameFlags.None, 1, okBlock);
                await p.SendAsync(Http2FrameType.Continuation, Http2FrameFlags.EndHeaders, 3, okBlock);
            }),
            ("HEADERS on a stream that was never opened", p =>
                p.SendAsync(Http2FrameType.Headers, Http2FrameFlags.EndHeaders, 3, okBlock)),
            ("HEADERS on stream 0", p =>
                p.SendAsync(Http2FrameType.Headers, Http2FrameFlags.EndHeaders, 0, okBlock)),
            ("DATA before the response HEADERS", p =>
                p.SendAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, "early"u8.ToArray())),
            ("PUSH_PROMISE, which the client disabled", p =>
                p.SendAsync(Http2FrameType.PushPromise, Http2FrameFlags.EndHeaders, 1, new byte[] { 0, 0, 0, 2 }.Concat(okBlock).ToArray())),
        };

        foreach (var (name, script) in cases)
        {
            await runner.RunAsync($"h2 client: {name} is a connection error", async () =>
            {
                await using var peer = new Peer(async p =>
                {
                    await p.WaitForRequestHeadAsync();
                    await script(p);
                });

                var failure = await FailureOfAsync(peer, null, Get());

                runner.IsTrue(failure is Http2ProtocolException { ErrorCode: Http2ErrorCode.ProtocolError },
                    $"PROTOCOL_ERROR (was: {Describe(failure)})");
                runner.AreEqual(Http2ErrorCode.ProtocolError, await peer.GoAwayCodeAsync(), "and the origin gets GOAWAY(PROTOCOL_ERROR)");
            });
        }

        await runner.RunAsync("h2 client: an HPACK error is COMPRESSION_ERROR", async () =>
        {
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendAsync(Http2FrameType.Headers, Http2FrameFlags.EndHeaders, 1, new byte[] { 0xFF, 0x80 }); // a truncated integer
            });

            var failure = await FailureOfAsync(peer, null, Get());

            runner.IsTrue(failure is Http2ProtocolException { ErrorCode: Http2ErrorCode.CompressionError },
                $"COMPRESSION_ERROR, as the server role reports it (was: {Describe(failure)})");
        });
    }

    // ------------------------------------------------------------------ flow control

    private static async Task RunFlowControlTestsAsync(TestRunner runner)
    {
        const int MaxWindow = int.MaxValue;

        // What the origin sends first, and what the client must say back.
        var rejected = new (string Name, Func<Peer, Task> Script, Http2ErrorCode Expected)[]
        {
            ("WINDOW_UPDATE of 0 on the connection", p => p.SendWindowUpdateAsync(0, 0), Http2ErrorCode.ProtocolError),
            ("WINDOW_UPDATE of 0 on the stream", p => p.SendWindowUpdateAsync(1, 0), Http2ErrorCode.ProtocolError),
            ("WINDOW_UPDATE of 0 on a stream that was never opened", p => p.SendWindowUpdateAsync(3, 0), Http2ErrorCode.ProtocolError),
            ("WINDOW_UPDATE of 1 on a stream that was never opened", p => p.SendWindowUpdateAsync(3, 1), Http2ErrorCode.ProtocolError),
            ("a connection window past 2^31-1", p => p.SendWindowUpdateAsync(0, MaxWindow), Http2ErrorCode.FlowControlError),
            ("a stream window past 2^31-1", p => p.SendWindowUpdateAsync(1, MaxWindow), Http2ErrorCode.FlowControlError),
            ("a connection window pushed over the top by one byte", async p =>
            {
                await p.SendWindowUpdateAsync(0, MaxWindow - 65_535); // exactly the maximum: legal
                await p.SendWindowUpdateAsync(0, 1);
            }, Http2ErrorCode.FlowControlError),
            ("a SETTINGS_INITIAL_WINDOW_SIZE of 2^31", p => p.SendSettingsAsync((4, 0x8000_0000u)), Http2ErrorCode.FlowControlError),
            ("a stream window pushed over the top by a SETTINGS change", async p =>
            {
                await p.SendSettingsAsync((4, (uint)MaxWindow)); // the stream window is now exactly the maximum: legal
                await p.SendWindowUpdateAsync(1, 1);
            }, Http2ErrorCode.FlowControlError),
        };

        // RFC 9113 §6.9: errors on a stream's own window reset the stream; the connection's close it.
        var streamErrors = new HashSet<string>
        {
            "WINDOW_UPDATE of 0 on the stream",
            "a stream window past 2^31-1",
            "a stream window pushed over the top by a SETTINGS change",
        };

        foreach (var (name, script, expected) in rejected)
        {
            await runner.RunAsync($"h2 client: {name} is refused", async () =>
            {
                await using var peer = new Peer(async p =>
                {
                    await p.WaitForRequestHeadAsync();
                    await script(p);
                });

                var failure = await FailureOfAsync(peer, null, Get());

                runner.IsTrue(failure is Http2ProtocolException ex && ex.ErrorCode == expected,
                    $"{expected} (was: {Describe(failure)})");
                if (streamErrors.Contains(name))
                {
                    runner.AreEqual(expected, await peer.RstStreamCodeAsync(1), "a stream error: the origin gets RST_STREAM with the same code");
                    runner.AreEqual(0, peer.GoAwaysReceived(), "and no GOAWAY");
                }
                else
                {
                    runner.AreEqual(expected, await peer.GoAwayCodeAsync(), "a connection error: the origin gets the same code in GOAWAY");
                }
            });
        }

        await runner.RunAsync("h2 client: windows exactly at 2^31-1 are accepted", async () =>
        {
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendWindowUpdateAsync(0, MaxWindow - 65_535);
                await p.SendWindowUpdateAsync(1, MaxWindow - 65_535);
                await p.SendHeaderBlockAsync(HpackEncoder.Encode([(":status", "204")]), Http2FrameFlags.EndStream);
            });

            var connection = new Http2ClientConnection(await peer.ConnectAsync());
            var head = await connection.SendRequestHeadAsync(Get(), peer.Ct).WaitAsync(HangLimit);

            runner.AreEqual(204, head.StatusCode, "the boundary itself is legal");
        });

        await runner.RunAsync("h2 client: a larger SETTINGS_INITIAL_WINDOW_SIZE lets an upload go on without a stream WINDOW_UPDATE", async () =>
        {
            // The connection window is raised by WINDOW_UPDATE(0), as it must be; the stream window
            // is raised only by the SETTINGS change. A client that ignores the change stops at 65,535.
            const int bodySize = 150_000;
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendSettingsAsync((4, 200_000));
                await p.SendWindowUpdateAsync(0, 1_000_000);
                await p.WaitForDataAsync(bodySize);
                await p.SendHeaderBlockAsync(HpackEncoder.Encode([(":status", "200")]), Http2FrameFlags.EndStream);
            });

            var connection = new Http2ClientConnection(await peer.ConnectAsync());
            var head = await connection.SendRequestHeadAsync(Post(bodySize), peer.Ct).WaitAsync(HangLimit);

            runner.AreEqual(200, head.StatusCode, "the origin answered once it had the whole body");
            runner.AreEqual((long)bodySize, peer.DataBytes(1), "every body byte was sent");
            runner.AreEqual(0, peer.WindowUpdatesFor(1), "and the client never had to be given stream credit");
        });

        await runner.RunAsync("h2 client: a smaller SETTINGS_INITIAL_WINDOW_SIZE takes the stream window negative", async () =>
        {
            const int bodySize = 100_000;
            long afterCredit = 0;
            long whileWaiting = 0;
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.WaitForDataAsync(65_535); // all the client could send before it heard anything
                await p.SendSettingsAsync((4, 1_000)); // the stream window is now 0 - 64,535
                await p.SendWindowUpdateAsync(0, 1_000_000);
                await p.SendWindowUpdateAsync(1, 64_535 + 500); // ... and now 500
                await p.WaitForDataAsync(65_535 + 500);
                await Task.Delay(300, p.Ct); // long enough for an over-sending client to show itself
                whileWaiting = p.DataBytes(1);
                await p.SendWindowUpdateAsync(1, 1_000_000);
                await p.WaitForDataAsync(bodySize);
                afterCredit = p.DataBytes(1);
                await p.SendHeaderBlockAsync(HpackEncoder.Encode([(":status", "200")]), Http2FrameFlags.EndStream);
            });

            var connection = new Http2ClientConnection(await peer.ConnectAsync());
            var head = await connection.SendRequestHeadAsync(Post(bodySize), peer.Ct).WaitAsync(HangLimit);

            runner.AreEqual(200, head.StatusCode, "the exchange completed");
            runner.AreEqual(65_535L + 500, whileWaiting, "the client sent exactly the 500 bytes of credit it was given");
            runner.AreEqual((long)bodySize, afterCredit, "and the rest once more credit came");
        });
    }

    // ------------------------------------------------------------------ GOAWAY

    private static async Task RunGoAwayTestsAsync(TestRunner runner)
    {
        var head = HpackEncoder.Encode([(":status", "200")]);

        await runner.RunAsync("h2 client: GOAWAY at or above the request's stream lets the response finish", async () =>
        {
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendHeaderBlockAsync(head, Http2FrameFlags.None);
                await p.SendAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, "part1-"u8.ToArray());
                await p.SendGoAwayAsync(lastStreamId: 1, Http2ErrorCode.NoError);
                await p.SendAsync(Http2FrameType.Data, Http2FrameFlags.EndStream, 1, "part2"u8.ToArray());
            });

            var connection = new Http2ClientConnection(await peer.ConnectAsync());
            var response = await connection.SendRequestHeadAsync(Get(), peer.Ct).WaitAsync(HangLimit);
            var body = await ReadAllAsync(connection.ResponseBody).WaitAsync(HangLimit);

            runner.AreEqual(200, response.StatusCode, "status");
            runner.AreEqual("part1-part2", body, "the stream the origin promised to finish was finished");
        });

        await runner.RunAsync("h2 client: a GOAWAY that arrives before the head does not cut a stream it still owns", async () =>
        {
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendGoAwayAsync(lastStreamId: 1, Http2ErrorCode.NoError);
                await p.SendHeaderBlockAsync(head, Http2FrameFlags.EndStream);
            });

            var connection = new Http2ClientConnection(await peer.ConnectAsync());
            var response = await connection.SendRequestHeadAsync(Get(), peer.Ct).WaitAsync(HangLimit);

            runner.AreEqual(200, response.StatusCode, "the response the origin still owed arrived");
        });

        await runner.RunAsync("h2 client: GOAWAY below the request's stream says the request was never processed", async () =>
        {
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendGoAwayAsync(lastStreamId: 0, Http2ErrorCode.NoError);
            });

            var failure = await FailureOfAsync(peer, null, Get());

            runner.IsTrue(failure is Http2GoAwayException { LastStreamId: 0, ErrorCode: Http2ErrorCode.NoError },
                $"a typed, replayable failure naming last-stream-id 0 (was: {Describe(failure)})");
        });

        await runner.RunAsync("h2 client: GOAWAY below the stream after it was answered is a connection error, not a replay signal", async () =>
        {
            foreach (var withPendingData in new[] { false, true })
            {
                await using var peer = new Peer(async p =>
                {
                    await p.WaitForRequestHeadAsync();
                    await p.SendHeaderBlockAsync(head, Http2FrameFlags.None);
                    if (withPendingData) await p.SendAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, "got it"u8.ToArray());
                    await p.SendGoAwayAsync(lastStreamId: 0, Http2ErrorCode.NoError);
                });

                var connection = new Http2ClientConnection(await peer.ConnectAsync());
                await connection.SendRequestHeadAsync(Get(), peer.Ct).WaitAsync(HangLimit);
                Exception? failure = null;
                try { await ReadAllAsync(connection.ResponseBody).WaitAsync(HangLimit); }
                catch (Exception ex) { failure = ex; }

                runner.IsTrue(failure is Http2ProtocolException { ErrorCode: Http2ErrorCode.ProtocolError },
                    $"{(withPendingData ? "with" : "without")} body data already read: PROTOCOL_ERROR, never Http2GoAwayException (was: {Describe(failure)})");
            }
        });

        await runner.RunAsync("h2 client: GOAWAY below the stream after an interim 1xx response is not a replay signal either", async () =>
        {
            // A 1xx proves the origin received the request; only a silent origin can claim it did not.
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendHeaderBlockAsync(HpackEncoder.Encode([(":status", "103"), ("link", "</a.css>; rel=preload")]), Http2FrameFlags.None);
                await p.SendGoAwayAsync(lastStreamId: 0, Http2ErrorCode.NoError);
            });

            var failure = await FailureOfAsync(peer, null, Get());

            runner.IsTrue(failure is Http2ProtocolException { ErrorCode: Http2ErrorCode.ProtocolError },
                $"PROTOCOL_ERROR, never Http2GoAwayException (was: {Describe(failure)})");
        });

        await runner.RunAsync("h2 client: a drained stream the origin then abandons is a failure, not a short success", async () =>
        {
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendHeaderBlockAsync(head, Http2FrameFlags.None);
                await p.SendAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, "half"u8.ToArray());
                await p.SendGoAwayAsync(lastStreamId: 1, Http2ErrorCode.NoError);
                p.CloseWire();
            });

            var connection = new Http2ClientConnection(await peer.ConnectAsync());
            await connection.SendRequestHeadAsync(Get(), peer.Ct).WaitAsync(HangLimit);
            Exception? failure = null;
            try { await ReadAllAsync(connection.ResponseBody).WaitAsync(HangLimit); }
            catch (Exception ex) { failure = ex; }

            runner.IsTrue(failure is IOException, $"the body ends in an IOException (was: {Describe(failure)})");
        });

        await runner.RunAsync("h2 client: GOAWAY that raises its last-stream-id is a connection error", async () =>
        {
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendGoAwayAsync(lastStreamId: 1, Http2ErrorCode.NoError);
                await p.SendGoAwayAsync(lastStreamId: 3, Http2ErrorCode.NoError);
            });

            var failure = await FailureOfAsync(peer, null, Get());

            runner.IsTrue(failure is Http2ProtocolException { ErrorCode: Http2ErrorCode.ProtocolError },
                $"PROTOCOL_ERROR (was: {Describe(failure)})");
            runner.AreEqual(Http2ErrorCode.ProtocolError, await peer.GoAwayCodeAsync(), "and the origin gets GOAWAY(PROTOCOL_ERROR)");
        });

        var malformed = new (string Name, Func<Peer, Task> Script, Http2ErrorCode Expected)[]
        {
            ("GOAWAY with a payload under 8 bytes", p => p.SendAsync(Http2FrameType.GoAway, Http2FrameFlags.None, 0, new byte[4]), Http2ErrorCode.FrameSizeError),
            ("GOAWAY on a stream", p => p.SendAsync(Http2FrameType.GoAway, Http2FrameFlags.None, 1, new byte[8]), Http2ErrorCode.ProtocolError),
        };

        foreach (var (name, script, expected) in malformed)
        {
            await runner.RunAsync($"h2 client: {name} is refused", async () =>
            {
                await using var peer = new Peer(async p =>
                {
                    await p.WaitForRequestHeadAsync();
                    await script(p);
                });

                var failure = await FailureOfAsync(peer, null, Get());

                runner.IsTrue(failure is Http2ProtocolException ex && ex.ErrorCode == expected, $"{expected} (was: {Describe(failure)})");
            });
        }
    }

    // ------------------------------------------------------------------ idle timeout

    private static async Task RunIdleTimeoutTestsAsync(TestRunner runner)
    {
        var idle = TimeSpan.FromMilliseconds(400);
        var head = HpackEncoder.Encode([(":status", "200")]);

        await runner.RunAsync("h2 client: an origin that never answers is cut by the idle timeout", async () =>
        {
            await using var peer = new Peer(p => p.WaitForRequestHeadAsync());

            var failure = await FailureOfAsync(peer, idle, Get());

            runner.IsTrue(failure is HttpParseException ex && ex.Message.Contains("stalled", StringComparison.Ordinal),
                $"a named stall, not a hang (was: {Describe(failure)})");
        });

        await runner.RunAsync("h2 client: PING keep-alives alone do not hold an unanswered request open", async () =>
        {
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                while (true)
                {
                    await p.SendAsync(Http2FrameType.Ping, Http2FrameFlags.None, 0, new byte[8]);
                    await Task.Delay(100, p.Ct);
                }
            });

            var failure = await FailureOfAsync(peer, idle, Get());

            runner.IsTrue(failure is HttpParseException, $"still cut (was: {Describe(failure)})");
        });

        await runner.RunAsync("h2 client: an upload the origin stops granting window for is cut", async () =>
        {
            await using var peer = new Peer(p => p.WaitForRequestHeadAsync());

            var failure = await FailureOfAsync(peer, idle, Post(150_000));

            runner.IsTrue(failure is HttpParseException, $"the send stalls on window and is cut (was: {Describe(failure)})");
        });

        await runner.RunAsync("h2 client: an upload an origin stops reading (TCP back-pressure) is cut", async () =>
        {
            // The window is granted in full, so the client never waits on window: it writes until the
            // socket buffers are full and the write itself stalls. The origin never reads.
            await using var peer = new Peer(async p =>
            {
                await p.SendSettingsAsync((4, (uint)int.MaxValue));
                await p.SendWindowUpdateAsync(0, int.MaxValue - 65_535);
                await Task.Delay(Timeout.Infinite, p.Ct);
            }, readFrames: false);

            var failure = await FailureOfAsync(peer, idle, Post(48_000_000));

            runner.IsTrue(failure is HttpParseException ex && ex.Message.Contains("stalled", StringComparison.Ordinal),
                $"the stalled write is cut (was: {Describe(failure)})");
        });

        await runner.RunAsync("h2 client: an origin that goes silent mid-body is cut on the body read", async () =>
        {
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendHeaderBlockAsync(head, Http2FrameFlags.None);
                await p.SendAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, "start"u8.ToArray());
            });

            var connection = new Http2ClientConnection(await peer.ConnectAsync(), idle);
            await connection.SendRequestHeadAsync(Get(), peer.Ct).WaitAsync(HangLimit);
            Exception? failure = null;
            try { await ReadAllAsync(connection.ResponseBody).WaitAsync(HangLimit); }
            catch (Exception ex) { failure = ex; }

            runner.IsTrue(failure is HttpParseException, $"cut on the read (was: {Describe(failure)})");
        });

        await runner.RunAsync("h2 client: empty DATA frames alone do not hold a body open", async () =>
        {
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendHeaderBlockAsync(head, Http2FrameFlags.None);
                while (true)
                {
                    await p.SendAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, ReadOnlyMemory<byte>.Empty);
                    await Task.Delay(20, p.Ct);
                }
            });

            var connection = new Http2ClientConnection(await peer.ConnectAsync(), idle);
            await connection.SendRequestHeadAsync(Get(), peer.Ct).WaitAsync(HangLimit);
            Exception? failure = null;
            try { await ReadAllAsync(connection.ResponseBody).WaitAsync(HangLimit); }
            catch (Exception ex) { failure = ex; }

            runner.IsTrue(failure is HttpParseException, $"cut (was: {Describe(failure)})");
        });

        await runner.RunAsync("h2 client: an idle timeout longer than the timer can hold means no timeout, not an exception", async () =>
        {
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendHeaderBlockAsync(head, Http2FrameFlags.EndStream);
            });

            var connection = new Http2ClientConnection(await peer.ConnectAsync(), TimeSpan.FromDays(40));
            var response = await connection.SendRequestHeadAsync(Get(), peer.Ct).WaitAsync(HangLimit);

            runner.AreEqual(200, response.StatusCode, "a 40-day timeout is accepted and the exchange works");
        });

        await runner.RunAsync("h2 client: header blocks after the head that do not end the stream are refused, trailers that do are fine", async () =>
        {
            var trailers = HpackEncoder.Encode([("x-checksum", "abc")]);

            await using var endless = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendHeaderBlockAsync(head, Http2FrameFlags.None);
                while (true) await p.SendHeaderBlockAsync(trailers, Http2FrameFlags.None);
            });
            var connection = new Http2ClientConnection(await endless.ConnectAsync(), idle);
            await connection.SendRequestHeadAsync(Get(), endless.Ct).WaitAsync(HangLimit);
            Exception? failure = null;
            try { await ReadAllAsync(connection.ResponseBody).WaitAsync(HangLimit); }
            catch (Exception ex) { failure = ex; }
            runner.IsTrue(failure is Http2ProtocolException { ErrorCode: Http2ErrorCode.ProtocolError },
                $"a trailers block without END_STREAM is PROTOCOL_ERROR (was: {Describe(failure)})");

            await using var proper = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendHeaderBlockAsync(head, Http2FrameFlags.None);
                await p.SendAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, "body"u8.ToArray());
                await p.SendHeaderBlockAsync(trailers, Http2FrameFlags.EndStream);
            });
            var ok = new Http2ClientConnection(await proper.ConnectAsync(), idle);
            await ok.SendRequestHeadAsync(Get(), proper.Ct).WaitAsync(HangLimit);
            runner.AreEqual("body", await ReadAllAsync(ok.ResponseBody).WaitAsync(HangLimit), "trailers that carry END_STREAM still end the body");
        });

        await runner.RunAsync("h2 client: a slow consumer is not mistaken for an idle origin", async () =>
        {
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendHeaderBlockAsync(head, Http2FrameFlags.None);
                await p.SendAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, "a"u8.ToArray());
                await p.SendAsync(Http2FrameType.Data, Http2FrameFlags.EndStream, 1, "b"u8.ToArray());
            });

            var connection = new Http2ClientConnection(await peer.ConnectAsync(), idle);
            await connection.SendRequestHeadAsync(Get(), peer.Ct).WaitAsync(HangLimit);
            await Task.Delay(idle * 2); // the origin has said everything; the reader is simply late
            var body = await ReadAllAsync(connection.ResponseBody).WaitAsync(HangLimit);

            runner.AreEqual("ab", body, "the body was read in full after a pause longer than the timeout");
        });

        await runner.RunAsync("h2 client: a slow origin that keeps sending is never cut", async () =>
        {
            // Each gap is well inside the timeout; the whole takes longer than it. The timer is
            // re-armed by every frame of real data, so this must complete.
            await using var peer = new Peer(async p =>
            {
                await p.WaitForRequestHeadAsync();
                await p.SendHeaderBlockAsync(head, Http2FrameFlags.None);
                for (var i = 0; i < 6; i++)
                {
                    await Task.Delay(150, p.Ct);
                    await p.SendAsync(Http2FrameType.Data, i == 5 ? Http2FrameFlags.EndStream : Http2FrameFlags.None, 1, "chunk;"u8.ToArray());
                }
            });

            var connection = new Http2ClientConnection(await peer.ConnectAsync(), idle);
            await connection.SendRequestHeadAsync(Get(), peer.Ct).WaitAsync(HangLimit);
            var body = await ReadAllAsync(connection.ResponseBody).WaitAsync(HangLimit);

            runner.AreEqual("chunk;chunk;chunk;chunk;chunk;chunk;", body, "the whole slow body arrived");
        });
    }

    // ------------------------------------------------------------------ through the proxy

    private static async Task RunProxyTimeoutTestAsync(TestRunner runner)
    {
        await runner.RunAsync("a silent HTTP/2 origin gives the client a 502 after the upstream idle timeout", async () =>
        {
            using var ca = CertificateAuthority.LoadOrCreate(
                Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-Http2ClientHardening-Certs"));
            var store = new SessionStore();
            var options = new ProxyOptions
            {
                Port = 0,
                DecryptHttps = true,
                ValidateUpstreamCertificates = false,
                UpstreamIdleTimeout = TimeSpan.FromMilliseconds(600),
            };
            await using var proxy = new ProxyServer(options, ca, store);
            proxy.Start();

            // The origin accepts the request and then never answers it.
            await using var origin = new TestHttp2Origin(ca.GetCertificateFor("127.0.0.1"),
                (Func<HttpRequestData, CancellationToken, Task<HttpResponseData>>)(async (_, ct) =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return HttpResponseData.Simple(200, "OK", "never");
                }));

            using var client = new HttpClient(new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{proxy.Endpoint!.Port}"),
                UseProxy = true,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => TrustsRoot(ca.RootCertificate, cert),
            })
            { Timeout = TimeSpan.FromSeconds(15) };

            using var response = await client.GetAsync($"https://127.0.0.1:{origin.Port}/silent");

            runner.AreEqual(HttpStatusCode.BadGateway, response.StatusCode, "the stalled upstream leg ends as a 502");

            var session = store.Snapshot().LastOrDefault(s => s.Path == "/silent");
            runner.IsTrue(session?.State == SessionState.Failed
                && session.Error?.Contains("stalled", StringComparison.Ordinal) == true,
                $"and the session records why (was: {session?.State} / {session?.Error})");
        });
    }

    // ------------------------------------------------------------------ helpers

    private static HttpRequestData Get() => new()
    {
        Method = "GET",
        RequestTarget = "/x",
        Url = new Uri("https://origin.test/x"),
        HttpVersion = "HTTP/1.1",
    };

    private static HttpRequestData Post(int bodySize)
    {
        var request = Get();
        request.Method = "POST";
        request.Body = new byte[bodySize];
        return request;
    }

    /// <summary>Runs one request to the point where it throws and returns what it threw. A hang
    /// past <see cref="HangLimit"/> comes back as a <see cref="TimeoutException"/>, so a client
    /// that never gives up fails the assertion instead of the run.</summary>
    private static async Task<Exception?> FailureOfAsync(Peer peer, TimeSpan? idleTimeout, HttpRequestData request)
    {
        var connection = new Http2ClientConnection(await peer.ConnectAsync(), idleTimeout);
        try
        {
            await connection.SendRequestHeadAsync(request, peer.Ct).WaitAsync(HangLimit);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static string Describe(Exception? ex) => ex is null ? "no exception" : $"{ex.GetType().Name}: {ex.Message}";

    private static async Task<string> ReadAllAsync(Stream body)
    {
        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer);
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    /// <summary>An HPACK integer with the H bit clear, for building string lengths by hand.</summary>
    private static IEnumerable<byte> HpackLength(int value, int prefixBits)
    {
        var max = (1 << prefixBits) - 1;
        if (value < max) return [(byte)value];
        var bytes = new List<byte> { (byte)max };
        value -= max;
        while (value >= 128)
        {
            bytes.Add((byte)((value % 128) + 128));
            value /= 128;
        }
        bytes.Add((byte)value);
        return bytes;
    }

    private static bool TrustsRoot(X509Certificate2 root, X509Certificate? presented)
    {
        if (presented is null) return false;
        using var leaf = new X509Certificate2(presented);
        using var chain = new X509Chain();
        chain.ChainPolicy.ExtraStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        if (!chain.Build(leaf)) return false;
        return chain.ChainElements.Cast<X509ChainElement>().Any(e => e.Certificate.Thumbprint == root.Thumbprint);
    }

    /// <summary>
    /// A scripted origin: one loopback connection, a background reader that keeps every frame the
    /// client sends, and helpers to send frames back. The script runs on its own task and may end
    /// early or be cut off when the test is done; only what the client does is asserted.
    /// </summary>
    private sealed class Peer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly TaskCompletionSource<Stream> _wire = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Http2Frame> _received = [];
        private readonly Lock _gate = new();
        private readonly Task _run;
        private TcpClient? _accepted;
        private TcpClient? _connected;

        /// <param name="script">What the origin does once the client has connected.</param>
        /// <param name="readFrames">False for an origin that never reads its socket, so the client's
        /// writes meet TCP back-pressure.</param>
        public Peer(Func<Peer, Task> script, bool readFrames = true)
        {
            _listener.Start();
            _run = Task.Run(async () =>
            {
                try
                {
                    _accepted = await _listener.AcceptTcpClientAsync(Ct).ConfigureAwait(false);
                    _accepted.NoDelay = true;
                    _wire.TrySetResult(_accepted.GetStream());
                    var reader = readFrames ? ReadLoopAsync() : Task.CompletedTask;
                    await script(this).ConfigureAwait(false);
                    await reader.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    // The client went away or the test ended: the scripted side has nothing to add.
                }
            });
        }

        public CancellationToken Ct => _cts.Token;

        /// <summary>The client's end of the connection.</summary>
        public async Task<Stream> ConnectAsync()
        {
            _connected = new TcpClient { NoDelay = true };
            await _connected.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port, Ct);
            return _connected.GetStream();
        }

        private async Task ReadLoopAsync()
        {
            var wire = await _wire.Task.ConfigureAwait(false);
            try
            {
                var preface = new byte[24];
                await wire.ReadExactlyAsync(preface, Ct).ConfigureAwait(false);
                while (await Http2FrameReader.ReadAsync(wire, 1 << 24, Ct).ConfigureAwait(false) is { } frame)
                {
                    lock (_gate) _received.Add(frame);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or Http2ProtocolException)
            {
                // The client closed or the test ended.
            }
        }

        public async Task SendAsync(Http2FrameType type, Http2FrameFlags flags, int streamId, ReadOnlyMemory<byte> payload) =>
            await Http2FrameWriter.WriteAsync(await _wire.Task.ConfigureAwait(false), type, flags, streamId, payload, Ct).ConfigureAwait(false);

        /// <summary>HEADERS then CONTINUATION frames of at most 16 KiB, END_HEADERS on the last.</summary>
        public async Task SendHeaderBlockAsync(byte[] block, Http2FrameFlags headersFlags, int chunkSize = 16_384)
        {
            for (var offset = 0; offset == 0 || offset < block.Length; offset += chunkSize)
            {
                var length = Math.Min(chunkSize, block.Length - offset);
                var last = offset + length >= block.Length;
                var flags = (offset == 0 ? headersFlags : Http2FrameFlags.None) | (last ? Http2FrameFlags.EndHeaders : Http2FrameFlags.None);
                await SendAsync(offset == 0 ? Http2FrameType.Headers : Http2FrameType.Continuation, flags, 1,
                    block.AsMemory(offset, length)).ConfigureAwait(false);
            }
        }

        public Task SendSettingsAsync(params (ushort Id, uint Value)[] settings)
        {
            var payload = new byte[settings.Length * 6];
            for (var i = 0; i < settings.Length; i++)
            {
                payload[i * 6] = (byte)(settings[i].Id >> 8);
                payload[i * 6 + 1] = (byte)settings[i].Id;
                payload[i * 6 + 2] = (byte)(settings[i].Value >> 24);
                payload[i * 6 + 3] = (byte)(settings[i].Value >> 16);
                payload[i * 6 + 4] = (byte)(settings[i].Value >> 8);
                payload[i * 6 + 5] = (byte)settings[i].Value;
            }
            return SendAsync(Http2FrameType.Settings, Http2FrameFlags.None, 0, payload);
        }

        public Task SendWindowUpdateAsync(int streamId, int increment) =>
            SendAsync(Http2FrameType.WindowUpdate, Http2FrameFlags.None, streamId,
                new[] { (byte)(increment >> 24), (byte)(increment >> 16), (byte)(increment >> 8), (byte)increment });

        public Task SendGoAwayAsync(int lastStreamId, Http2ErrorCode code)
        {
            var payload = new byte[8];
            payload[0] = (byte)(lastStreamId >> 24);
            payload[1] = (byte)(lastStreamId >> 16);
            payload[2] = (byte)(lastStreamId >> 8);
            payload[3] = (byte)lastStreamId;
            payload[7] = (byte)(uint)code;
            return SendAsync(Http2FrameType.GoAway, Http2FrameFlags.None, 0, payload);
        }

        public void CloseWire() => _accepted?.Close();

        /// <summary>Returns once the client's HEADERS (the request head) has been read.</summary>
        public async Task WaitForRequestHeadAsync() =>
            await Poll.UntilAsync(() => Count(f => f.Type == Http2FrameType.Headers) > 0).ConfigureAwait(false);

        public long DataBytes(int streamId)
        {
            lock (_gate) return _received.Where(f => f.Type == Http2FrameType.Data && f.StreamId == streamId).Sum(f => (long)f.DataPayload.Length);
        }

        public Task WaitForDataAsync(long bytes) => Poll.UntilAsync(() => DataBytes(1) >= bytes);

        public int WindowUpdatesFor(int streamId) =>
            Count(f => f.Type == Http2FrameType.WindowUpdate && f.StreamId == streamId);

        /// <summary>The error code of the GOAWAY the client sent, or NoError if none arrived.</summary>
        public async Task<Http2ErrorCode> GoAwayCodeAsync()
        {
            await Poll.UntilAsync(() => Count(f => f.Type == Http2FrameType.GoAway) > 0, 3_000).ConfigureAwait(false);
            lock (_gate)
            {
                var goAway = _received.FirstOrDefault(f => f.Type == Http2FrameType.GoAway);
                if (goAway.Payload.Length < 8) return Http2ErrorCode.NoError;
                var s = goAway.Payload.Span;
                return (Http2ErrorCode)(((uint)s[4] << 24) | ((uint)s[5] << 16) | ((uint)s[6] << 8) | s[7]);
            }
        }

        public int GoAwaysReceived() => Count(f => f.Type == Http2FrameType.GoAway);

        /// <summary>The error code of the RST_STREAM the client sent for a stream, or NoError if none arrived.</summary>
        public async Task<Http2ErrorCode> RstStreamCodeAsync(int streamId)
        {
            await Poll.UntilAsync(() => Count(f => f.Type == Http2FrameType.RstStream && f.StreamId == streamId) > 0, 3_000).ConfigureAwait(false);
            lock (_gate)
            {
                var rst = _received.FirstOrDefault(f => f.Type == Http2FrameType.RstStream && f.StreamId == streamId);
                if (rst.Payload.Length < 4) return Http2ErrorCode.NoError;
                var s = rst.Payload.Span;
                return (Http2ErrorCode)(((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3]);
            }
        }

        private int Count(Func<Http2Frame, bool> match)
        {
            lock (_gate) return _received.Count(match);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            _accepted?.Dispose();
            _connected?.Dispose();
            try { await _run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { /* a script stuck in a write; the socket is gone with the process */ }
            _cts.Dispose();
        }
    }
}
