using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.IO;
using System.Xml.Linq;

namespace KeyCreator
{
    internal class Program
    {
        static void ShowHelp()
        {
            Console.WriteLine("""
                    usage:
                      KeyCreator new [name] - create a new public and private keypair with name (name should preferrably be mod name)
                      KeyCreator sign [DLL] [private key] - create a signature using the given private key
                      KeyCreator test [DLL] [public key] [sig] - calculate the DLL's signature with the public key and test if it matches
                    """);
        }

        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                ShowHelp();
                return 1;
            }

            switch (args[0])
            {
                default:
                    ShowHelp();
                    return 1;

                case "new":
                    return CreateNewKey(args[1]);
                case "sign":
                    return SignDLL(args[1], args[2]);
                case "test":
                    return TestDLL(args[1], args[2], args[3]);
            }
        }

        static int CreateNewKey(string name)
        {
            var rsa = RSA.Create(4096);
            File.WriteAllText($"{name}.priv", rsa.ToXmlString(true));
            var pub = rsa.ExportParameters(false);
            static string b64(byte[] bytes) => Convert.ToBase64String(bytes);
            File.WriteAllText($"{name}.pub", $"{name} {b64(pub.Modulus)}|{b64(pub.Exponent)}");

            Console.WriteLine($"Wrote keys to {name}.priv and {name}.pub.");
            
            return 0;
        }

        static int SignDLL(string dll, string privkey)
        {
            using var rsa = new RSACryptoServiceProvider();
            rsa.FromXmlString(File.ReadAllText(privkey));
            File.WriteAllBytes($"{dll}.sig", rsa.SignData(File.ReadAllBytes(dll), SHA256.Create()));
            Console.WriteLine($"Wrote signature to {dll}.sig.");
            return 0;
        }

        static int TestDLL(string dll, string pubkey, string sig)
        {
            using var rsa = new RSACryptoServiceProvider();
            static byte[] db64(string str) => Convert.FromBase64String(str);
            var key = File.ReadAllText(pubkey).Split().Last().Split('|');
            var param = new RSAParameters
            {
                Modulus = db64(key[0]),
                Exponent = db64(key[1]),
            };
            
            rsa.ImportParameters(param); 
            var test = rsa.VerifyData(File.ReadAllBytes(dll), SHA256.Create(), File.ReadAllBytes(sig));
            if (test)
            {
                Console.WriteLine("The signature matches with the provided public key.");
                return 0;
            }
            Console.WriteLine("The signature does *not* match with the provided public key.");
            return 1;
        }
    }
}
