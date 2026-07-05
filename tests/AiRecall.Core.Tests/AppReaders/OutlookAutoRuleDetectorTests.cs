using AiRecall.AppReader.Outlook;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer <see cref="OutlookAutoRuleDetector"/> (Spec 0004 Iter. 3).
/// Alle Heuristik-Bedingungen plus die Mindestens-2-Regel werden getestet.
/// </summary>
public class OutlookAutoRuleDetectorTests
{
    private static MailSnapshot FreshMail(string subject = "Hello", string from = "alice@example.com") =>
        new(
            Subject: subject,
            From: from,
            FolderName: "Inbox",
            UnRead: true,
            ReceivedTime: new DateTimeOffset(2026, 7, 5, 10, 0, 0, TimeSpan.FromHours(2)),
            LastModificationTime: new DateTimeOffset(2026, 7, 5, 10, 0, 0, TimeSpan.FromHours(2)),
            Body: "Hi Martin,\n\nWie geht's?\n\nGruss, Alice");

    [Fact]
    public void IsSuspect_FreshUnreadMail_ReturnsFalse()
    {
        var mail = FreshMail();
        Assert.False(OutlookAutoRuleDetector.IsSuspect(mail));
    }

    [Fact]
    public void IsSuspect_MarkedReadImmediately_AloneNotEnough()
    {
        // Bedingung 1: UnRead=false, LastModificationTime == ReceivedTime
        var base1 = FreshMail();
        var mail = base1 with
        {
            UnRead = false,
            LastModificationTime = base1.ReceivedTime,
        };
        // Nur 1 Bedingung — Mindestens-2-Regel schlaegt nicht zu
        Assert.False(OutlookAutoRuleDetector.IsSuspect(mail));
    }

    [Fact]
    public void IsSuspect_MarkedReadImmediately_PlusNoReply_ReturnsTrue()
    {
        // Bedingung 1 (Read+fast) + Bedingung 3 (noreply)
        var base3 = FreshMail(from: "noreply@newsletter.example.com");
        var mail = base3 with
        {
            UnRead = false,
            LastModificationTime = base3.ReceivedTime.AddSeconds(2),
        };
        Assert.True(OutlookAutoRuleDetector.IsSuspect(mail));
        Assert.Equal("1+3", OutlookAutoRuleDetector.Explain(mail));
    }

    [Fact]
    public void IsSuspect_InNewsletterFolder_AloneNotEnough()
    {
        // Bedingung 2: Newsletter-Folder
        var mail = FreshMail() with { FolderName = "Newsletter" };
        Assert.False(OutlookAutoRuleDetector.IsSuspect(mail));
    }

    [Fact]
    public void IsSuspect_InNewsletterFolder_PlusNoReply_ReturnsTrue()
    {
        // Folder "Newsletter" + Sender mit NoReply-Prefix "notifications@".
        var mail = FreshMail(from: "notifications@example.com") with { FolderName = "Newsletter" };
        Assert.True(OutlookAutoRuleDetector.IsSuspect(mail));
        Assert.Equal("2+3", OutlookAutoRuleDetector.Explain(mail));
    }

    [Fact]
    public void IsSuspect_InJunkFolder_AloneNotEnough()
    {
        var mail = FreshMail() with { FolderName = "Junk E-Mail" };
        Assert.False(OutlookAutoRuleDetector.IsSuspect(mail));
    }

    [Fact]
    public void IsSuspect_InDeletedItems_PlusNoReply_ReturnsTrue()
    {
        // "Deleted Items" ist im JunkFolderNames-Set, plus NoReply-Sender.
        var mail = FreshMail(from: "noreply@bad.example.com") with { FolderName = "Deleted Items" };
        Assert.True(OutlookAutoRuleDetector.IsSuspect(mail));
        Assert.Equal("2+3", OutlookAutoRuleDetector.Explain(mail));
    }

    [Fact]
    public void IsSuspect_NoreplySender_AloneNotEnough()
    {
        // Bedingung 3: noreply@ — aber kein zweiter Trigger
        var mail = FreshMail(from: "noreply@github.com");
        Assert.False(OutlookAutoRuleDetector.IsSuspect(mail));
    }

    [Fact]
    public void IsSuspect_NoReplySender_VariousPrefixes()
    {
        var prefixes = new[] { "noreply@", "no-reply@", "notifications@", "mailer-daemon@" };
        foreach (var prefix in prefixes)
        {
            var mail = FreshMail(from: prefix + "example.com");
            Assert.True(OutlookAutoRuleDetector.Condition3_NoReplySender(mail),
                $"Condition3 should match for prefix '{prefix}'");
        }
    }

    [Fact]
    public void IsSuspect_RealPersonEmail_DoesNotMatchNoReply()
    {
        var mail = FreshMail(from: "alice.mueller@example.com");
        Assert.False(OutlookAutoRuleDetector.Condition3_NoReplySender(mail));
    }

    [Fact]
    public void IsSuspect_AutoReplySubjectAndBody_ReturnsTrue()
    {
        // Bedingung 4: AW:-Prefix + Out-of-Office im Body
        var mail = FreshMail(
            subject: "AW: Termin-Anfrage",
            from: "alice@example.com") with
        {
            Body = "Vielen Dank fuer Ihre Nachricht. Ich bin bis 15.07. im Urlaub (Out of Office).",
        };
        Assert.True(OutlookAutoRuleDetector.Condition4_AutoReplySubjectAndBody(mail));
    }

    [Fact]
    public void IsSuspect_AutoReplySubject_WithoutBodyIndicator_NotEnough()
    {
        // Bedingung 4 verlangt BEIDE: Subject-Prefix UND Body-Indikator
        var mail = FreshMail(subject: "AW: Frage") with
        {
            Body = "Hallo, koennen wir telefonieren? Gruss",
        };
        Assert.False(OutlookAutoRuleDetector.Condition4_AutoReplySubjectAndBody(mail));
        // Auch alleine kein Suspect
        Assert.False(OutlookAutoRuleDetector.IsSuspect(mail));
    }

    [Fact]
    public void IsSuspect_BodyIndicator_WithoutReplySubject_NotEnough()
    {
        var mail = FreshMail(subject: "Sommerfest im Buero") with
        {
            Body = "Auto-Reply wird gesendet.",
        };
        Assert.False(OutlookAutoRuleDetector.Condition4_AutoReplySubjectAndBody(mail));
    }

    [Fact]
    public void IsSuspect_AutoReply_BothSubs_PlusJunkFolder_ReturnsTrue()
    {
        // Bedingung 2 + 4
        var mail = FreshMail(
            subject: "WG: Meeting-Verschiebung",
            from: "boss@example.com") with
        {
            FolderName = "Notifications",
            Body = "Automatische Antwort: Ich bin heute nicht im Buero.",
        };
        Assert.True(OutlookAutoRuleDetector.IsSuspect(mail));
        Assert.Equal("2+4", OutlookAutoRuleDetector.Explain(mail));
    }

    [Fact]
    public void IsSuspect_AllFourConditions_ReturnsTrue()
    {
        var base4 = FreshMail(
            subject: "AW: Newsletter-Abo",
            from: "noreply@news.example.com");
        var mail = base4 with
        {
            FolderName = "Newsletter",
            UnRead = false,
            LastModificationTime = base4.ReceivedTime.AddSeconds(1),
            Body = "Auto-Reply: Diese Mail wird nicht gelesen.",
        };
        Assert.True(OutlookAutoRuleDetector.IsSuspect(mail));
        Assert.Equal("1+2+3+4", OutlookAutoRuleDetector.Explain(mail));
    }

    [Fact]
    public void IsSuspect_FolderPrefixRegex_CaseInsensitive()
    {
        // Folder-Prefix-Regex: case-insensitive, plus Word-Boundary (\b)
        // damit "Newsletters" (Plural) nicht matched. Mit Trennzeichen
        // (Space, Bindestrich) matcht es.
        Assert.True(OutlookAutoRuleDetector.Condition2_JunkOrAutoFolder(FreshMail() with { FolderName = "newsletter" }));
        Assert.True(OutlookAutoRuleDetector.Condition2_JunkOrAutoFolder(FreshMail() with { FolderName = "AUTO-Archive" }));
        Assert.True(OutlookAutoRuleDetector.Condition2_JunkOrAutoFolder(FreshMail() with { FolderName = "Rule Actions" }));
        // "RuleActions" (zusammengesetzt) matcht nicht wegen Word-Boundary
        Assert.False(OutlookAutoRuleDetector.Condition2_JunkOrAutoFolder(FreshMail() with { FolderName = "RuleActions" }));
    }

    [Fact]
    public void IsSuspect_FolderDoesNotMatch_ReturnsFalse()
    {
        // Aehnlich aber kein Match (z. B. "Customer Projects")
        Assert.False(OutlookAutoRuleDetector.Condition2_JunkOrAutoFolder(FreshMail() with { FolderName = "Customer Projects" }));
        Assert.False(OutlookAutoRuleDetector.Condition2_JunkOrAutoFolder(FreshMail() with { FolderName = "Archive 2025" }));
    }

    [Fact]
    public void IsSuspect_NullMail_ReturnsFalse()
    {
        Assert.False(OutlookAutoRuleDetector.IsSuspect(null!));
        Assert.Equal(string.Empty, OutlookAutoRuleDetector.Explain(null!));
    }

    [Fact]
    public void IsSuspect_EmptyFields_NoConditionsMet()
    {
        var mail = new MailSnapshot(
            Subject: "",
            From: "",
            FolderName: "",
            UnRead: true,
            ReceivedTime: default,
            LastModificationTime: default,
            Body: "");
        Assert.False(OutlookAutoRuleDetector.IsSuspect(mail));
        Assert.Equal(string.Empty, OutlookAutoRuleDetector.Explain(mail));
    }

    [Fact]
    public void IsSuspect_ModificationTimeSlightlyOlderThanReceived_ReturnsFalse()
    {
        // Edge-Case: LastModificationTime < ReceivedTime (sollte nie
        // passieren, aber defensiv). Bedingung 1 erfordert delta >= 0.
        var base5 = FreshMail();
        var mail = base5 with
        {
            UnRead = false,
            ReceivedTime = base5.ReceivedTime.AddSeconds(10),
            LastModificationTime = base5.ReceivedTime, // vor ReceivedTime
        };
        Assert.False(OutlookAutoRuleDetector.Condition1_MarkedReadImmediately(mail));
    }
}