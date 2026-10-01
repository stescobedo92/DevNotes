using System.Globalization;
using DevNotes.Application.Notes;
using DevNotes.Application.Tests.Fakes;
using DevNotes.Domain.Notes;

namespace DevNotes.Application.Tests.Notes;

public sealed class NoteTemplateTests
{
    private static readonly DateOnly _today = new(2026, 9, 30);
    private static readonly NoteId _id = NoteId.Parse("01J8ZQ4M9T3N7K5W2X6Y8V0B1C");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("bug", "bug")]
    [InlineData("Bug resuelto", "bug-resuelto")]
    [InlineData("  Décision   d'architecture ", "decision-d-architecture")]
    public void TemplateKey_TryNormalize_ProducesASlug(string name, string expected)
    {
        TemplateKey.TryNormalize(name, out var key).Should().BeTrue();
        key.Should().Be(expected);
        TemplateKey.IsValid(key).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData("¡¡¡")]
    public void TemplateKey_TryNormalize_RejectsNamesWithoutLettersOrDigits(string? name)
    {
        TemplateKey.TryNormalize(name, out _).Should().BeFalse();
    }

    [Fact]
    public void TemplateKey_TryNormalize_RejectsOverlongNames()
    {
        TemplateKey.TryNormalize(new string('a', TemplateKey.MaxLength + 1), out _).Should().BeFalse();
        TemplateKey.IsValid("Bug").Should().BeFalse("only the normalized form is a key");
        TemplateKey.IsValid("../bug").Should().BeFalse();
    }

    [Fact]
    public void Render_SubstitutesEveryPlaceholder_AndDropsEmptyFrontmatterKeys()
    {
        const string template = "---\nid: {{id}}\ntitle: {{ TITLE }}\nproject: {{project}}\ntype: {{type}}\ncreated: {{date}}\n---\n\n# {{title}}\n\n{{body}}\n";

        var text = NoteTemplates.Render(template, new TemplateContext(_id, "Deadlock: inventory", null, _today, NoteType.Bug, "Captured text"));

        text.Should().Be($"---\nid: {_id.Value}\ntitle: \"Deadlock: inventory\"\ntype: bug\ncreated: 2026-09-30\n---\n\n# Deadlock: inventory\n\nCaptured text\n");
        var document = NoteDocumentParser.Parse(text, "f");
        document.FrontmatterStatus.Should().Be(FrontmatterStatus.Valid);
        document.Metadata.Project.Should().BeNull();
        document.Metadata.Type.Should().Be(NoteType.Bug);
    }

    [Fact]
    public void Render_WithProject_KeepsTheKey()
    {
        var text = NoteTemplates.Render("---\nproject: {{project}}\n---\n", new TemplateContext(_id, "T", " azure micro ", _today, NoteType.Note));

        text.Should().Be("---\nproject: azure micro\n---\n");
    }

    [Fact]
    public void Render_BodyWithoutPlaceholder_IsAppendedAfterABlankLine()
    {
        var text = NoteTemplates.Render("---\ntitle: {{title}}\n---\n# {{title}}", new TemplateContext(_id, "T", null, _today, NoteType.Note, "line one\nline two\n"));

        text.Should().Be("---\ntitle: T\n---\n# T\n\nline one\nline two\n");
    }

    [Fact]
    public void Render_EmptyBody_LeavesNoTraceOfThePlaceholder()
    {
        var text = NoteTemplates.Render("# {{title}}\n\n{{body}}\n", new TemplateContext(_id, "T", null, _today, NoteType.Note));

        text.Should().Be("# T\n\n");
    }

    [Fact]
    public void Render_UnknownPlaceholdersAndStrayBraces_AreLeftAlone()
    {
        const string template = "Use {{unknown}} and {{ not closed\n{{title}} {{}}\n";

        var text = NoteTemplates.Render(template, new TemplateContext(_id, "T", null, _today, NoteType.Note));

        text.Should().Be("Use {{unknown}} and {{ not closed\nT {{}}\n");
    }

    [Fact]
    public void Render_ValuesAreYamlSafe()
    {
        var text = NoteTemplates.Render("---\ntitle: {{title}}\nproject: {{project}}\n---\n", new TemplateContext(_id, "a: b # c", "x: y", _today, NoteType.Note));

        var metadata = NoteDocumentParser.Parse(text, "f").Metadata;
        metadata.Title.Should().Be("a: b # c");
        metadata.Project.Should().Be("x: y");
    }

    [Fact]
    public void Render_TemplateWithByteOrderMark_IsHandled()
    {
        var text = NoteTemplates.Render("﻿---\nproject: {{project}}\ntitle: {{title}}\n---\n", new TemplateContext(_id, "T", null, _today, NoteType.Note));

        text.Should().Be("---\ntitle: T\n---\n");
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    public void BuiltIn_EveryTemplateRendersToAValidNoteOfItsType(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        foreach (var key in BuiltInTemplates.Keys)
        {
            var context = new TemplateContext(_id, "Title", "proj", _today, BuiltInTemplates.TypeOf(key), "captured");
            var text = NoteTemplates.Render(BuiltInTemplates.Get(key, culture), context);
            var document = NoteDocumentParser.Parse(text, "f");

            document.FrontmatterStatus.Should().Be(FrontmatterStatus.Valid, $"template {key} in {cultureName}");
            document.Metadata.Should().BeEquivalentTo(
                new { Id = _id, Title = "Title", Project = "proj", Type = BuiltInTemplates.TypeOf(key), Created = _today, Updated = _today },
                options => options.ExcludingMissingMembers());
            document.Body.Should().StartWith("\n# Title\n").And.Contain("captured").And.EndWith("\n").And.NotContain("{{");
        }

        BuiltInTemplates.Get("bug", CultureInfo.GetCultureInfo("es")).Should().Contain("## Síntoma");
        BuiltInTemplates.Get("bug", CultureInfo.GetCultureInfo("en")).Should().Contain("## Symptom");
    }

    [Fact]
    public async Task Service_ListsBuiltInsFirst_ThenCustomOnes_WithOverridesApplied()
    {
        var store = new InMemoryTemplateStore();
        await store.SaveAsync("bug", "---\ntype: bug\n---\n# {{title}}\nMy bug\n", Ct);
        await store.SaveAsync("standup", "# {{title}}\n", Ct);
        await store.SaveAsync("adr-2", "---\ntype: adr\n---\n", Ct);
        var service = new TemplateService(store);

        var templates = await service.ListAsync(Ct);

        templates.Select(template => template.Key).Should().Equal("note", "bug", "adr", "runbook", "learning", "snippet", "adr-2", "standup");
        templates.Single(template => template.Key == "bug").Should().Match<NoteTemplate>(template =>
            template.IsBuiltIn && template.IsCustomized && template.Type == NoteType.Bug && template.Text.Contains("My bug"));
        templates.Single(template => template.Key == "note").Should().Match<NoteTemplate>(template => template.IsBuiltIn && !template.IsCustomized);
        templates.Single(template => template.Key == "standup").Should().Match<NoteTemplate>(template =>
            !template.IsBuiltIn && template.IsCustomized && template.Type == NoteType.Note);
        templates.Single(template => template.Key == "adr-2").Type.Should().Be(NoteType.Adr);
    }

    [Fact]
    public async Task Service_GetSaveAndReset()
    {
        var store = new InMemoryTemplateStore();
        var service = new TemplateService(store);

        (await service.GetAsync("bug", Ct))!.IsCustomized.Should().BeFalse();
        (await service.GetAsync("missing", Ct)).Should().BeNull();
        (await service.GetAsync("../bug", Ct)).Should().BeNull();

        var saved = await service.SaveAsync("bug", "---\r\ntype: bug\r\n---\r\n# {{title}}", Ct);

        saved.Text.Should().Be("---\ntype: bug\n---\n# {{title}}\n", "line endings are normalized and a final newline added");
        store.Templates["bug"].Should().Be(saved.Text);
        (await service.GetAsync("bug", Ct))!.IsCustomized.Should().BeTrue();

        await service.ResetAsync("bug", Ct);

        store.Templates.Should().NotContainKey("bug");
        (await service.GetAsync("bug", Ct))!.IsCustomized.Should().BeFalse();
    }

    [Fact]
    public async Task Service_RejectsBadKeysAndOversizedTemplates()
    {
        var service = new TemplateService(new InMemoryTemplateStore());

        await FluentActions.Invoking(() => service.SaveAsync("Bad Key", "x", Ct)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => service.ResetAsync("", Ct)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => service.SaveAsync("big", new string('x', TemplateService.MaxTemplateLength + 1), Ct))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void CreateDefault_StillProducesTheMinimalNote()
    {
        var text = NoteTemplates.CreateDefault(_id, "T", NoteType.Runbook, "p", _today);

        text.Should().Be($"---\nid: {_id.Value}\ntitle: T\nproject: p\ntags: []\ntype: runbook\ncreated: 2026-09-30\nupdated: 2026-09-30\n---\n\n# T\n\n");
    }
}
