﻿using System;
using System.Linq;

using Moq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ICSharpCode.SharpZipLib.Zip;

using CKAN;
using CKAN.NetKAN.Model;
using CKAN.NetKAN.Services;
using CKAN.NetKAN.Transformers;

namespace Tests.NetKAN.Transformers
{
    [TestFixture]
    public sealed class InternalCkanTransformerTests
    {
        private TransformOptions opts = new TransformOptions(1, null, null, false, null);

        [Test]
        public void AddsMissingProperties()
        {
            // Arrange
            const string filePath = "/DoesNotExist.zip";

            var internalCkan = new JObject();
            internalCkan["spec_version"] = 1;
            internalCkan["foo"] = "bar";

            var mHttp = new Mock<IHttpService>();
            var mModuleService = new Mock<IModuleService>();

            mHttp.Setup(i => i.DownloadModule(It.IsAny<Metadata>()))
                .Returns(filePath);

            mModuleService.Setup(i => i.GetInternalCkan(
                    It.IsAny<CkanModule>(), It.IsAny<string>(),
                    It.IsAny<GameInstance>()))
                .Returns(internalCkan);

            var sut = new InternalCkanTransformer(mHttp.Object, mModuleService.Object);

            var json = new JObject();
            json["spec_version"] = 1;
            json["identifier"] = "DoesNotExist";
            json["version"] = "1.0";
            json["download"] = "https://awesomemod.example/AwesomeMod.zip";

            // Act
            var result = sut.Transform(new Metadata(json), opts).First();
            var transformedJson = result.Json();

            // Assert
            Assert.That((string)transformedJson["foo"], Is.EqualTo("bar"),
                "InternalCkanTransformer should add properties from the internal ckan that don't exist on the original."
            );
        }

        [Test]
        public void DoesNotOverrideExistingProperties()
        {
            // Arrange
            const string filePath = "/DoesNotExist.zip";

            var internalCkan = new JObject();
            internalCkan["spec_version"] = 1;
            internalCkan["foo"] = "bar";

            var mHttp = new Mock<IHttpService>();
            var mModuleService = new Mock<IModuleService>();

            mHttp.Setup(i => i.DownloadModule(It.IsAny<Metadata>()))
                .Returns(filePath);

            mModuleService.Setup(i => i.GetInternalCkan(
                    It.IsAny<CkanModule>(), It.IsAny<string>(),
                    It.IsAny<GameInstance>()))
                .Returns(internalCkan);

            var sut = new InternalCkanTransformer(mHttp.Object, mModuleService.Object);

            var json = new JObject();
            json["spec_version"] = 1;
            json["identifier"] = "DoesNotExist";
            json["version"] = "1.0";
            json["foo"] = "baz";
            json["download"] = "https://awesomemod.example/AwesomeMod.zip";

            // Act
            var result = sut.Transform(new Metadata(json), opts).First();
            var transformedJson = result.Json();

            // Assert
            Assert.That((string)transformedJson["foo"], Is.EqualTo("baz"),
                "InternalCkanTransformer should not override existing properties."
            );
        }

        [TestCase("v1.2", "v1.4", "v1.4")]
        [TestCase("v1.4", "v1.2", "v1.4")]
        public void HigherOfTwoSpecVersionsIsChosen(
            string specVersion, string internalSpecVersion, string expectedSpecVersion
        )
        {
            // Arrange
            const string filePath = "/DoesNotExist.zip";

            var internalCkan = new JObject();
            internalCkan["spec_version"] = internalSpecVersion;

            var mHttp = new Mock<IHttpService>();
            var mModuleService = new Mock<IModuleService>();

            mHttp.Setup(i => i.DownloadModule(It.IsAny<Metadata>()))
                .Returns(filePath);

            mModuleService.Setup(i => i.GetInternalCkan(
                    It.IsAny<CkanModule>(), It.IsAny<string>(),
                    It.IsAny<GameInstance>()))
                .Returns(internalCkan);

            var sut = new InternalCkanTransformer(mHttp.Object, mModuleService.Object);

            var json = new JObject();
            json["spec_version"] = specVersion;
            json["identifier"] = "DoesNotExist";
            json["version"] = "1.0";
            json["download"] = "https://awesomemod.example/AwesomeMod.zip";

            // Act
            var result = sut.Transform(new Metadata(json), opts).First();
            var transformedJson = result.Json();

            // Assert
            Assert.That((string)transformedJson["spec_version"], Is.EqualTo(expectedSpecVersion),
                "InternalCkanTransformer should use the higher of the two spec_versions."
            );
        }
    }
}
