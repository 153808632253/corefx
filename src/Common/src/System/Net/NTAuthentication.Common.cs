// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication.ExtendedProtection;

namespace System.Net
{
    internal partial class NTAuthentication
    {
        private bool _isServer;

        private SafeFreeCredentials _credentialsHandle;
        private SafeDeleteContext _securityContext;
        private string _spn;

        private int _tokenSize;
        private ContextFlagsPal _requestedContextFlags;
        private ContextFlagsPal _contextFlags;

        private bool _isCompleted;
        private string _package;
        private string _lastProtocolName;
        private string _protocolName;
        private string _clientSpecifiedSpn;

        private ChannelBinding _channelBinding;

        // If set, no more calls should be made.
        internal bool IsCompleted => _isCompleted;
        internal bool IsValidContext => !(_securityContext == null || _securityContext.IsInvalid);
        internal string Package => _package;

        // True indicates this instance is for Server and will use AcceptSecurityContext SSPI API.
        internal bool IsServer => _isServer;

        internal string ClientSpecifiedSpn
        {
            get
            {
                if (_clientSpecifiedSpn == null)
                {
                    _clientSpecifiedSpn = GetClientSpecifiedSpn();
                }

                return _clientSpecifiedSpn;
            }
        }

        internal string ProtocolName
        {
            get
            {
                // Note: May return string.Empty if the auth is not done yet or failed.
                if (_protocolName == null)
                {
                    string negotiationAuthenticationPackage = null;

                    if (IsValidContext)
                    {
                        negotiationAuthenticationPackage = NegotiateStreamPal.QueryContextAuthenticationPackage(_securityContext);
                        if (IsCompleted)
                        {
                            _protocolName = negotiationAuthenticationPackage;
                        }
                    }

                    return negotiationAuthenticationPackage ?? string.Empty;
                }

                return _protocolName;
            }
        }

        internal bool IsKerberos
        {
            get
            {
                if (_lastProtocolName == null)
                {
                    _lastProtocolName = ProtocolName;
                }

                return (object)_lastProtocolName == (object)NegotiationInfoClass.Kerberos;
            }
        }

        //
        // This overload does not attempt to impersonate because the caller either did it already or the original thread context is still preserved.
        //
        internal NTAuthentication(bool isServer, string package, NetworkCredential credential, string spn, ContextFlagsPal requestedContextFlags, ChannelBinding channelBinding)
        {
            Initialize(isServer, package, credential, spn, requestedContextFlags, channelBinding);
        }

        private void Initialize(bool isServer, string package, NetworkCredential credential, string spn, ContextFlagsPal requestedContextFlags, ChannelBinding channelBinding)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this, package, spn, requestedContextFlags);

            _tokenSize = NegotiateStreamPal.QueryMaxTokenSize(package);
            _isServer = isServer;
            _spn = spn;
            _securityContext = null;
            _requestedContextFlags = requestedContextFlags;
            _package = package;
            _channelBinding = channelBinding;

            if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"Peer SPN-> '{_spn}'");

            //
            // Check if we're using DefaultCredentials.
            //

            Debug.Assert(CredentialCache.DefaultCredentials == CredentialCache.DefaultNetworkCredentials);
            if (credential == CredentialCache.DefaultCredentials)
            {
                if (NetEventSource.IsEnabled) NetEventSource.Info(this, "using DefaultCredentials");
                _credentialsHandle = NegotiateStreamPal.AcquireDefaultCredential(package, _isServer);
            }
            else
            {
                _credentialsHandle = NegotiateStreamPal.AcquireCredentialsHandle(package, _isServer, credential);
            }
        }

        internal SafeDeleteContext GetContext(out SecurityStatusPal status)
        {
            status = new SecurityStatusPal(SecurityStatusPalErrorCode.OK);
            if (!(IsCompleted && IsValidContext))
            {
                NetEventSource.Fail(this, "Should be called only when completed with success, currently is not!");
            }

            if (!IsServer)
            {
                NetEventSource.Fail(this, "The method must not be called by the client side!");
            }

            if (!IsValidContext)
            {
                status = new SecurityStatusPal(SecurityStatusPalErrorCode.InvalidHandle);
                return null;
            }

            return _securityContext;
        }

        internal void CloseContext()
        {
            if (_securityContext != null && !_securityContext.IsClosed)
            {
                _securityContext.Dispose();
            }
        }

        // SmtpNegotiate
        internal int VerifySignature(byte[] buffer, int offset, int count)
        {
            return NegotiateStreamPal.VerifySignature(_securityContext, buffer, offset, count);
        }

        // SmtpNegotiate
        internal int MakeSignature(byte[] buffer, int offset, int count, ref byte[] output)
        {
            return NegotiateStreamPal.MakeSignature(_securityContext, buffer, offset, count, ref output);
        }

        // SmtpNtlm, SmtpNegotiate, SocketHttpHandler
        internal string GetOutgoingBlob(string incomingBlob)
        {
            SecurityStatusPal statusCode;
            string outgoingBlob = GetOutgoingBlob(incomingBlob, out statusCode);

            if (statusCode.IsError)
            {
                Exception exception = NegotiateStreamPal.CreateExceptionFromError(statusCode);
                if (NetEventSource.IsEnabled)
                    NetEventSource.Exit(this, exception);
                throw exception;
            }

            return outgoingBlob;
        }

        // HttpListener
        internal string GetOutgoingBlob(string incomingBlob, out NegotiationError error)
        {
            error = NegotiationError.None;
            var outgoingBlog = GetOutgoingBlob(incomingBlob, out SecurityStatusPal statusCode);
            if (statusCode.IsError)
            {
                error = NegotiationErrorFromSecurityStatus(statusCode.ErrorCode);
            }
            return outgoingBlog;
        }

        // Upstack
        private string GetOutgoingBlob(string incomingBlob, out SecurityStatusPal statusCode)
        {
            byte[] decodedIncomingBlob = null;
            if (incomingBlob != null && incomingBlob.Length > 0)
            {
                decodedIncomingBlob = Convert.FromBase64String(incomingBlob);
            }
            byte[] decodedOutgoingBlob = null;

            if ((IsValidContext || IsCompleted) && decodedIncomingBlob == null)
            {
                // we tried auth previously, now we got a null blob, we're done. this happens
                // with Kerberos & valid credentials on the domain but no ACLs on the resource
                _isCompleted = true;
                statusCode = default;
            }
            else
            {
                decodedOutgoingBlob = GetOutgoingBlob(decodedIncomingBlob, out statusCode);
            }

            string outgoingBlob = null;
            if (decodedOutgoingBlob != null && decodedOutgoingBlob.Length > 0)
            {
                outgoingBlob = Convert.ToBase64String(decodedOutgoingBlob);
            }

            return outgoingBlob;
        }

        // NegotiateStream, upstack
        // Accepts an incoming binary security blob and returns an outgoing binary security blob.
        internal byte[] GetOutgoingBlob(byte[] incomingBlob, out SecurityStatusPal statusCode)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this, incomingBlob);

            var result = new byte[_tokenSize];

            bool firstTime = _securityContext == null;
            try
            {
                if (!_isServer)
                {
                    // client session
                    statusCode = NegotiateStreamPal.InitializeSecurityContext(
                        ref _credentialsHandle,
                        ref _securityContext,
                        _spn,
                        _requestedContextFlags,
                        incomingBlob,
                        _channelBinding,
                        ref result,
                        ref _contextFlags);

                    if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"SSPIWrapper.InitializeSecurityContext() returns statusCode:0x{((int)statusCode.ErrorCode):x8} ({statusCode})");

                    if (statusCode.ErrorCode == SecurityStatusPalErrorCode.CompleteNeeded)
                    {
                        statusCode = NegotiateStreamPal.CompleteAuthToken(ref _securityContext, result);

                        if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"SSPIWrapper.CompleteAuthToken() returns statusCode:0x{((int)statusCode.ErrorCode):x8} ({statusCode})");

                        result = null;
                    }
                }
                else
                {
                    // Server session.
                    statusCode = NegotiateStreamPal.AcceptSecurityContext(
                        _credentialsHandle,
                        ref _securityContext,
                        _requestedContextFlags,
                        incomingBlob,
                        _channelBinding,
                        ref result,
                        ref _contextFlags);

                    if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"SSPIWrapper.AcceptSecurityContext() returns statusCode:0x{((int)statusCode.ErrorCode):x8} ({statusCode})");
                }
            }
            finally
            {
                //
                // Assuming the ISC or ASC has referenced the credential on the first successful call,
                // we want to decrement the effective ref count by "disposing" it.
                // The real dispose will happen when the security context is closed.
                // Note if the first call was not successful the handle is physically destroyed here.
                //
                if (firstTime)
                {
                    _credentialsHandle?.Dispose();
                }
            }

            if (statusCode.IsError)
            {
                CloseContext();
                _isCompleted = true;

                if (NetEventSource.IsEnabled) NetEventSource.Exit(this, $"null statusCode:0x{((int)statusCode.ErrorCode):x8} ({statusCode})");
                return null;
            }
            else if (firstTime && _credentialsHandle != null)
            {
                // Cache until it is pushed out by newly incoming handles.
                SSPIHandleCache.CacheCredential(_credentialsHandle);
            }

            // The return value will tell us correctly if the handshake is over or not
            if (statusCode.ErrorCode == SecurityStatusPalErrorCode.OK
                || (_isServer && statusCode.ErrorCode == SecurityStatusPalErrorCode.CompleteNeeded))
            {
                // Success.
                _isCompleted = true;
            }
            else if (NetEventSource.IsEnabled)
            {
                // We need to continue.
                if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"need continue statusCode:0x{((int)statusCode.ErrorCode):x8} ({statusCode}) _securityContext:{_securityContext}");
            }

            if (NetEventSource.IsEnabled)
            {
                if (NetEventSource.IsEnabled) NetEventSource.Exit(this, $"IsCompleted: {IsCompleted}");
            }

            return result;
        }

        // This only works for context-destroying errors.
        internal static NegotiationError NegotiationErrorFromSecurityStatus(SecurityStatusPalErrorCode statusErrorCode)
        {
            if (IsCredentialFailure(statusErrorCode))
            {
                return NegotiationError.Credential;
            }
            if (IsClientFault(statusErrorCode))
            {
                return NegotiationError.InvalidOperation;
            }
            return NegotiationError.Unknown;
        }

        // This only works for context-destroying errors.
        private static bool IsCredentialFailure(SecurityStatusPalErrorCode error)
        {
            return error == SecurityStatusPalErrorCode.LogonDenied ||
                error == SecurityStatusPalErrorCode.UnknownCredentials ||
                error == SecurityStatusPalErrorCode.NoImpersonation ||
                error == SecurityStatusPalErrorCode.NoAuthenticatingAuthority ||
                error == SecurityStatusPalErrorCode.UntrustedRoot ||
                error == SecurityStatusPalErrorCode.CertExpired ||
                error == SecurityStatusPalErrorCode.SmartcardLogonRequired ||
                error == SecurityStatusPalErrorCode.BadBinding;
        }

        // This only works for context-destroying errors.
        private static bool IsClientFault(SecurityStatusPalErrorCode error)
        {
            return error == SecurityStatusPalErrorCode.InvalidToken ||
                error == SecurityStatusPalErrorCode.CannotPack ||
                error == SecurityStatusPalErrorCode.QopNotSupported ||
                error == SecurityStatusPalErrorCode.NoCredentials ||
                error == SecurityStatusPalErrorCode.MessageAltered ||
                error == SecurityStatusPalErrorCode.OutOfSequence ||
                error == SecurityStatusPalErrorCode.IncompleteMessage ||
                error == SecurityStatusPalErrorCode.IncompleteCredentials ||
                error == SecurityStatusPalErrorCode.WrongPrincipal ||
                error == SecurityStatusPalErrorCode.TimeSkew ||
                error == SecurityStatusPalErrorCode.IllegalMessage ||
                error == SecurityStatusPalErrorCode.CertUnknown ||
                error == SecurityStatusPalErrorCode.AlgorithmMismatch ||
                error == SecurityStatusPalErrorCode.SecurityQosFailed ||
                error == SecurityStatusPalErrorCode.UnsupportedPreauth;
        }

        private string GetClientSpecifiedSpn()
        {
            if (!(IsValidContext && IsCompleted))
            {
                NetEventSource.Fail(this, "Trying to get the client SPN before handshaking is done!");
            }

            string spn = NegotiateStreamPal.QueryContextClientSpecifiedSpn(_securityContext);

            if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"The client specified SPN is [{spn}]");

            return spn;
        }
    }
}
