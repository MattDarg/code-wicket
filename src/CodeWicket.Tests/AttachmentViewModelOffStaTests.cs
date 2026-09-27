using System;
using System.Threading;
using System.Windows.Threading;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A message's attachments can be carried by code that runs on no dispatcher: prompt delivery
    /// holds them from Enter until the prompt goes out, and its tests are plain xunit with no STA
    /// thread. That holds only while building one and reading what delivery reads touches no WPF
    /// object - the <see cref="AttachmentViewModel.Thumbnail"/> decode is the one thing that does, and
    /// it is lazy.
    /// </summary>
    public sealed class AttachmentViewModelOffStaTests
    {
        // A real 1x1 PNG, so a decode that did run would succeed and create WPF objects rather than
        // fail quietly on garbage and hide that it ran.
        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        /// <summary>
        /// On a FRESH MTA thread, so the evidence is unambiguous: any WPF <c>DispatcherObject</c> created
        /// there - a decoded <c>BitmapSource</c> is one - gives that thread a dispatcher, and a thread
        /// that has never run anything else cannot have one from an earlier test.
        /// </summary>
        [Fact]
        public void BuildingAndReadingAnAttachmentTouchesNoWpfObject()
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());

                    var attachment = new AttachmentViewModel("snip.png", "image/png", Png, remove: _ => { });

                    Assert.Equal("snip.png", attachment.Name);
                    Assert.Equal("image/png", attachment.MimeType);
                    Assert.Same(Png, attachment.Bytes);
                    Assert.Null(attachment.FilePath);
                    attachment.NoteSaved(@"C:\attachments\snip.png");
                    Assert.Equal(@"C:\attachments\snip.png", attachment.FilePath);
                    var dto = attachment.ToDto();
                    Assert.NotNull(dto);
                    Assert.Equal(Convert.ToBase64String(Png), dto!.Data);
                    Assert.NotNull(attachment.Detach().Bytes);

                    Assert.Null(Dispatcher.FromThread(Thread.CurrentThread));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            thread.Join();

            if (failure is not null)
                throw new Xunit.Sdk.XunitException("off-STA attachment body failed: " + failure);
        }
    }
}
