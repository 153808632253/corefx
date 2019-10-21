﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Authentication.ExtendedProtection;
using System.Security.Principal;

namespace System.Net.Security
{
    public class NegotiateAuthState : IDisposable
    {
        private readonly NTAuthentication _ntAuth;

        public NegotiateAuthState(bool isServer, string package, NetworkCredential credential, string spn, NegotiateAuthFlags requestedContextFlags, ChannelBinding channelBinding)
        {
            _ntAuth = new NTAuthentication(isServer, package, credential, spn, (ContextFlagsPal)requestedContextFlags, channelBinding);
        }

        public bool IsCompleted => _ntAuth.IsCompleted;
        // The package used for negotiation (Negotiate, NTLM)
        public string Package => _ntAuth.Package;
        // The negotiated protocol (Kerberos, NTLM)
        public string ProtocolName => _ntAuth.ProtocolName;
        public string ClientSpecifiedSpn => _ntAuth.ClientSpecifiedSpn;

        // Smtp, HttpClient
        public string GetOutgoingBlob(string incomingBlob) => _ntAuth.GetOutgoingBlob(incomingBlob);

        // HttpListener
        public string GetOutgoingBlob(string incomingBlob, out NegotiationError error)
            => _ntAuth.GetOutgoingBlob(incomingBlob, out error);

        // SmtpNegotiate only
        public int MakeSignature(byte[] buffer, int offset, int count, ref byte[] output)
            => _ntAuth.MakeSignature(buffer, offset, count, ref output);

        // SmtpNegotiate only
        public int VerifySignature(byte[] buffer, int offset, int count)
            => _ntAuth.VerifySignature(buffer, offset, count);

        public IIdentity GetIdentity() => NegotiateStreamPal.GetIdentity(_ntAuth);

        public NegotiationError TryGetIdentity(out IIdentity identity) => NegotiateStreamPal.TryGetIdentity(_ntAuth, out identity);

        public void Dispose()
        {
            _ntAuth.CloseContext();
        }
    }

    // See ContextFlagsPal

    [Flags]
    public enum NegotiateAuthFlags
    {
        None = 0,
        Delegate = 0x00000001,
        MutualAuth = 0x00000002,
        ReplayDetect = 0x00000004,
        SequenceDetect = 0x00000008,
        Confidentiality = 0x00000010,
        UseSessionKey = 0x00000020,
        AllocateMemory = 0x00000100,
        Connection = 0x00000800,
        InitExtendedError = 0x00004000,
        AcceptExtendedError = 0x00008000,
        InitStream = 0x00008000,
        AcceptStream = 0x00010000,
        InitIntegrity = 0x00010000,
        AcceptIntegrity = 0x00020000,
        InitManualCredValidation = 0x00080000,
        InitUseSuppliedCreds = 0x00000080,
        InitIdentify = 0x00020000,
        AcceptIdentify = 0x00080000,
        ProxyBindings = 0x04000000,
        AllowMissingBindings = 0x10000000,
        UnverifiedTargetName = 0x20000000,
    }

    public static class NegotiationPackages
    {
        public static readonly string NTLM = "NTLM";
        public static readonly string Negotiate = "Negotiate";
    }

    public static class NegotiationProtocols
    {
        public static readonly string NTLM = "NTLM";
        public static readonly string Kerberos = "Kerberos";
    }

    public enum NegotiationError
    {
        None,
        Credential,
        InvalidOperation,
        Unknown
    }
}
