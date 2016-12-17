﻿using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;
using MirrorSharp.Internal.Reflection;
using MirrorSharp.Internal.Results;

namespace MirrorSharp.Internal.Handlers {
    public class TypeCharHandler : ICommandHandler {
        public IImmutableList<char> CommandIds { get; } = ImmutableList.Create('C');

        public async Task ExecuteAsync(ArraySegment<byte> data, WorkSession session, ICommandResultSender sender, CancellationToken cancellationToken) {
            var @char = FastConvert.Utf8ByteArrayToChar(data);
            session.SourceText = session.SourceText.WithChanges(
                new TextChange(new TextSpan(session.CursorPosition, 0), FastConvert.CharToString(@char))
            );
            session.CursorPosition += 1;

            await CheckCompletionAsync(@char, session, sender, cancellationToken).ConfigureAwait(false);
            await CheckSignatureHelpAsync(@char, session, sender, cancellationToken).ConfigureAwait(false);
        }

        private async Task CheckSignatureHelpAsync(char @char, WorkSession session, ICommandResultSender sender, CancellationToken cancellationToken) {
            var trigger = new SignatureHelpTriggerInfoData(SignatureHelpTriggerReason.TypeCharCommand, @char);
            if (session.CurrentSignatureHelp != null) {
                var provider = session.CurrentSignatureHelp.Value.Provider;
                if (provider.IsRetriggerCharacter(@char)) {
                    session.CurrentSignatureHelp = null;
                    await SendSignatureHelpAsync(null, sender, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await TryApplySignatureHelpAsync(provider, trigger, session, sender, cancellationToken, sendIfEmpty: true).ConfigureAwait(false);
                return;
            }

            foreach (var provider in session.SignatureHelpProviders) {
                if (await TryApplySignatureHelpAsync(provider, trigger, session, sender, cancellationToken).ConfigureAwait(false))
                    return;
            }
        }

        private async Task<bool> TryApplySignatureHelpAsync(ISignatureHelpProviderWrapper provider, SignatureHelpTriggerInfoData trigger, WorkSession session, ICommandResultSender sender, CancellationToken cancellationToken, bool sendIfEmpty = false) {
            if (!provider.IsTriggerCharacter(trigger.TriggerCharacter.Value))
                return false;

            var help = await provider.GetItemsAsync(session.Document, session.CursorPosition, trigger, cancellationToken).ConfigureAwait(false);
            if (!sendIfEmpty && help == null)
                return false;

            session.CurrentSignatureHelp = help != null ? new CurrentSignatureHelp(provider, help) : (CurrentSignatureHelp?)null;
            await SendSignatureHelpAsync(help, sender, cancellationToken).ConfigureAwait(false);
            return true;
        }

        private Task CheckCompletionAsync(char @char, WorkSession session, ICommandResultSender sender, CancellationToken cancellationToken) {
            if (session.CurrentCompletionList != null)
                return TaskEx.CompletedTask;

            var trigger = CompletionTrigger.CreateInsertionTrigger(@char);
            if (!session.CompletionService.ShouldTriggerCompletion(session.SourceText, session.CursorPosition, trigger))
                return TaskEx.CompletedTask;

            return TriggerCompletionAsync(session, sender, cancellationToken, trigger);
        }

        private async Task TriggerCompletionAsync(WorkSession session, ICommandResultSender sender, CancellationToken cancellationToken, CompletionTrigger trigger) {
            session.CurrentCompletionList = await session.CompletionService.GetCompletionsAsync(session.Document, session.CursorPosition, trigger, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (session.CurrentCompletionList == null)
                return;

            await SendCompletionListAsync(session.CurrentCompletionList, sender, cancellationToken).ConfigureAwait(false);
        }

        private Task SendCompletionListAsync(CompletionList completionList, ICommandResultSender sender, CancellationToken cancellationToken) {
            var writer = sender.StartJsonMessage("completions");
            writer.WritePropertyName("span");
            // ReSharper disable once PossibleNullReferenceException
            writer.WriteSpan(completionList.DefaultSpan);
            writer.WritePropertyStartArray("completions");
            foreach (var item in completionList.Items) {
                writer.WriteStartObject();
                writer.WriteProperty("filterText", item.FilterText);
                writer.WriteProperty("displayText", item.DisplayText);
                writer.WritePropertyStartArray("tags");
                foreach (var tag in item.Tags) {
                    writer.WriteValue(tag.ToLowerInvariant());
                }
                writer.WriteEndArray();
                if (item.Span != completionList.DefaultSpan) {
                    writer.WritePropertyName("span");
                    writer.WriteSpan(item.Span);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            return sender.SendJsonMessageAsync(cancellationToken);
        }

        private Task SendSignatureHelpAsync([CanBeNull] SignatureHelpItemsData items, ICommandResultSender sender, CancellationToken cancellationToken) {
            var writer = sender.StartJsonMessage("signatures");
            if (items == null)
                return sender.SendJsonMessageAsync(cancellationToken);

            var selectedItemIndex = items.SelectedItemIndex;
            writer.WritePropertyName("span");
            writer.WriteSpan(items.ApplicableSpan);
            writer.WritePropertyStartArray("signatures");
            var itemIndex = 0;
            foreach (var item in items.Items) {
                writer.WriteStartObject();
                if (selectedItemIndex == null && items.ArgumentCount <= item.ParameterCount)
                    selectedItemIndex = itemIndex;
                if (itemIndex == selectedItemIndex)
                    writer.WriteProperty("selected", true);
                writer.WritePropertyStartArray("parts");
                writer.WriteSymbolDisplayParts(item.PrefixDisplayParts);
                var parameterIndex = 0;
                foreach (var parameter in item.Parameters) {
                    if (parameterIndex > 0)
                        writer.WriteSymbolDisplayParts(item.SeparatorDisplayParts);
                    var selected = items.ArgumentIndex == parameterIndex;
                    writer.WriteSymbolDisplayParts(parameter.PrefixDisplayParts, selected);
                    writer.WriteSymbolDisplayParts(parameter.DisplayParts, selected);
                    writer.WriteSymbolDisplayParts(parameter.SuffixDisplayParts, selected);
                    parameterIndex += 1;
                }
                writer.WriteSymbolDisplayParts(item.SuffixDisplayParts);
                writer.WriteEndArray();
                writer.WriteEndObject();
                itemIndex += 1;
            }
            writer.WriteEndArray();
            return sender.SendJsonMessageAsync(cancellationToken);
        }

        public bool CanChangeSession => true;
    }
}
