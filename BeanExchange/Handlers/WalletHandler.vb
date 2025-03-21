﻿Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports Org.BouncyCastle.Asn1.X9
Imports Org.BouncyCastle.Crypto
Imports Org.BouncyCastle.Crypto.EC
Imports Org.BouncyCastle.Crypto.Generators
Imports Org.BouncyCastle.Crypto.Parameters
Imports Org.BouncyCastle.Math
Imports Org.BouncyCastle.Security

Public Class WalletHandler
    Private Shared ReadOnly curve As X9ECParameters = ECNamedCurveTable.GetByName("secp256k1")
    Private Shared ReadOnly domainParams As ECDomainParameters = New ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H)
    Private Shared msInput As MemoryStream
    Private Shared msOutput As MemoryStream

    Public Shared Function GenerateWallet() As Tuple(Of String, String)
        ' Generate the ECDSA key pair
        Dim keyPair As Tuple(Of String, String) = GenerateKeyPair()
        Dim privateKey As String = keyPair.Item1
        Dim publicKey As String = keyPair.Item2

        ' Return the public key
        Return Tuple.Create(publicKey, privateKey)
    End Function

    Public Shared Function GenerateKeyPair() As Tuple(Of String, String)
        ' Generate ECDSA key pair
        Dim generator As New ECKeyPairGenerator("ECDSA")
        generator.Init(New ECKeyGenerationParameters(domainParams, New SecureRandom()))
        Dim keyPair As AsymmetricCipherKeyPair = generator.GenerateKeyPair()

        ' Extract private and public keys
        Dim privateKey As ECPrivateKeyParameters = TryCast(keyPair.Private, ECPrivateKeyParameters)
        Dim publicKey As ECPublicKeyParameters = TryCast(keyPair.Public, ECPublicKeyParameters)

        ' Encode private key to Base64 string
        Dim privateKeyString As String = Convert.ToBase64String(privateKey.D.ToByteArrayUnsigned())

        ' Encode public key to Base64 string in compressed format
        Dim publicKeyString As String = Convert.ToBase64String(publicKey.Q.GetEncoded(True))

        Return New Tuple(Of String, String)(privateKeyString, publicKeyString)
    End Function

    Public Shared Function SignTransaction(privateKey As String, transactionData As String) As String
        Try
            ' Decode the private key
            Dim privateKeyBytes As Byte() = Convert.FromBase64String(privateKey)
            Dim keyParameters As New ECPrivateKeyParameters(New BigInteger(1, privateKeyBytes), domainParams)

            ' Create a SHA256withECDSA signer
            Dim signer As ISigner = SignerUtilities.GetSigner("SHA256withECDSA")

            ' Initialize the signer for signing
            signer.Init(True, keyParameters)

            Dim bytes As Byte() = Encoding.UTF8.GetBytes(transactionData)

            ' Update the signer with the transaction data
            signer.BlockUpdate(bytes, 0, bytes.Length)

            ' Generate the signature
            Dim signature As Byte() = signer.GenerateSignature()

            ' Encode the signature to Base64 string
            Return Convert.ToBase64String(signature)

        Catch ex As Exception
            Console.WriteLine($"Error signing transaction: {ex.Message}")
            Return ""
        End Try
    End Function

    Public Shared Function GetPublicKeyFromPrivateKey(privateKeyString As String) As String
        Try
            ' Decode the private key
            Dim privateKeyBytes As Byte() = Convert.FromBase64String(privateKeyString)

            ' Get the ECDomainParameters
            Dim ecP As X9ECParameters = CustomNamedCurves.GetByName("secp256k1")
            Dim parameters As ECDomainParameters = New ECDomainParameters(ecP.Curve, ecP.G, ecP.N, ecP.H, ecP.GetSeed())

            ' Create ECPrivateKeyParameters from the private key bytes
            Dim privateKey As New ECPrivateKeyParameters(
                "ECDSA",
                New BigInteger(1, privateKeyBytes),
                parameters)

            ' Calculate the public key from the private key
            Dim q As Org.BouncyCastle.Math.EC.ECPoint = privateKey.Parameters.G.Multiply(privateKey.D)

            ' Create ECPublicKeyParameters from the calculated public key point
            Dim publicKey As New ECPublicKeyParameters(
                "ECDSA",
                q,
                parameters)

            ' Encode public key to Base64 string in compressed format
            Dim publicKeyString As String = Convert.ToBase64String(publicKey.Q.GetEncoded(True))

            Return publicKeyString

        Catch ex As FormatException
            ' Return the error message or handle it as needed
            Return "Error: Invalid format - " & ex.Message
        Catch ex As Exception
            ' Handle other exceptions if necessary
            Return "Error: " & ex.Message
        End Try
    End Function

    Public Shared Function EncryptPrivateKey(privateKey As String, encryptionKey As String) As Tuple(Of String, String)
        Using aesAlg As New AesManaged()
            ' 1. Generate the key
            Using sha256 As SHA256 = SHA256.Create()
                Dim keyBytes As Byte() = sha256.ComputeHash(Encoding.UTF8.GetBytes(encryptionKey))
                aesAlg.Key = keyBytes
            End Using

            ' 2. Generate a random IV
            aesAlg.GenerateIV()
            Dim ivString As String = Convert.ToBase64String(aesAlg.IV)

            ' 3. Encrypt
            Dim encryptor As ICryptoTransform = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV)
            Using msEncrypt As New MemoryStream()
                Using csEncrypt As New CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write)
                    Using swEncrypt As New StreamWriter(csEncrypt)
                        swEncrypt.Write(privateKey)
                    End Using
                    Dim encryptedBytes As Byte() = msEncrypt.ToArray()
                    Dim encryptedKey As String = Convert.ToBase64String(encryptedBytes)

                    Return Tuple.Create(encryptedKey, ivString)
                End Using
            End Using
        End Using
    End Function

    Public Shared Function DecryptPrivateKey(encryptedPrivateKey As String, iv As String, encryptionKey As String) As String
        Using aesAlg As New AesManaged()
            ' 1. Generate the key
            Using sha256 As SHA256 = SHA256.Create()
                Dim keyBytes As Byte() = sha256.ComputeHash(Encoding.UTF8.GetBytes(encryptionKey))
                aesAlg.Key = keyBytes
            End Using

            ' 2. Convert the IV from Base64
            aesAlg.IV = Convert.FromBase64String(iv)

            ' 3. Decrypt
            Dim decryptor As ICryptoTransform = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV)
            Using msDecrypt As New MemoryStream(Convert.FromBase64String(encryptedPrivateKey))
                Using csDecrypt As New CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read)
                    Using srDecrypt As New StreamReader(csDecrypt)
                        Return srDecrypt.ReadToEnd()
                    End Using
                End Using
            End Using
        End Using
    End Function

    ' Password Hashing
    Public Shared Function HashPassword(password As String) As String
        ' Use BCrypt.NET for password hashing
        Return BCrypt.Net.BCrypt.HashPassword(password)
    End Function

    Public Shared Function VerifyPassword(password As String, hash As String) As Boolean
        ' Use BCrypt.NET for password verification
        Return BCrypt.Net.BCrypt.Verify(password, hash)
    End Function

    Public Enum CryptoAction
        ActionEncrypt = 1
        ActionDecrypt = 2
    End Enum

    Private Shared Function EncryptOrDecryptFile(strInput As String, strOutputFile As String, bytKey() As Byte, bytIV() As Byte, Direction As CryptoAction) As Boolean
        Try
            ' Create a MemoryStream with the input string
            msInput = New MemoryStream(System.Text.Encoding.UTF8.GetBytes(strInput))

            ' Create a MemoryStream for output
            msOutput = New MemoryStream()

            ' Declare variables for encrypt/decrypt process
            Dim bytBuffer(4096) As Byte
            Dim lngBytesProcessed As Long = 0
            Dim lngFileLength As Long = msInput.Length
            Dim intBytesInCurrentBlock As Integer
            Dim csCryptoStream As CryptoStream

            ' Declare your CryptoServiceProvider
            Dim cspRijndael As New RijndaelManaged()

            ' Determine if encryption or decryption and setup CryptoStream
            Select Case Direction
                Case CryptoAction.ActionEncrypt
                    csCryptoStream = New CryptoStream(msOutput, cspRijndael.CreateEncryptor(bytKey, bytIV), CryptoStreamMode.Write)
                    Exit Select
                Case CryptoAction.ActionDecrypt
                    csCryptoStream = New CryptoStream(msOutput, cspRijndael.CreateDecryptor(bytKey, bytIV), CryptoStreamMode.Write)
                    Exit Select
            End Select

            ' Use While to loop until all of the string is processed
            While lngBytesProcessed < lngFileLength
                ' Read data from the input MemoryStream
                intBytesInCurrentBlock = msInput.Read(bytBuffer, 0, 4096)

                ' Write output to the cryptostream
                csCryptoStream.Write(bytBuffer, 0, intBytesInCurrentBlock)

                ' Update lngBytesProcessed
                lngBytesProcessed = lngBytesProcessed + CLng(intBytesInCurrentBlock)
            End While

            ' Close streams
            csCryptoStream.Close()
            msInput.Close()
            msOutput.Close()

            ' If encrypting, save the encrypted data to a file
            If Direction = CryptoAction.ActionEncrypt Then
                File.WriteAllBytes(strOutputFile, msOutput.ToArray())
            End If

            ' If decrypting, return the decrypted string
            If Direction = CryptoAction.ActionDecrypt Then
                Return System.Text.Encoding.UTF8.GetString(msOutput.ToArray())
            End If

            Return True
        Catch ex As Exception
            ' Handle any errors that occur during encryption or decryption
            Return False
        End Try
    End Function
End Class