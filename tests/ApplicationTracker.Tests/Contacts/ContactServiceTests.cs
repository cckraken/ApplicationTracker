using System.ComponentModel.DataAnnotations;
using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Contacts;
using ApplicationTracker.Core.Domain;
using ApplicationTracker.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace ApplicationTracker.Tests.Contacts;

public sealed class ContactServiceTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 21, 9, 30, 0, TimeSpan.Zero));
    private readonly ApplicationService _applications;
    private readonly ContactService _contacts;

    public ContactServiceTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _applications = new ApplicationService(_db, _time);
        _contacts = new ContactService(_db);
    }

    public void Dispose() => _db.Dispose();

    private Task<int> CreateApplication(string company = "Contoso", string role = "Analyst") =>
        _applications.CreateAsync(new ApplicationInput { Company = company, RoleTitle = role });

    private static ContactInput Input(string name = "Alex Doe", string? company = null, string? role = null) =>
        new() { Name = name, Company = company, Role = role };

    private async Task<IReadOnlyList<ContactSummary>> ContactsOf(int applicationId) =>
        (await _applications.GetDetailsAsync(applicationId))!.Contacts;

    private async Task<List<string>> CompanyNames()
    {
        using var db = _db.CreateDbContext();
        return await db.Companies.OrderBy(c => c.Name).Select(c => c.Name).ToListAsync();
    }

    [Fact]
    public async Task Create_trims_input_and_blanks_become_null()
    {
        var id = await _contacts.CreateAsync(new ContactInput
        {
            Name = "  Alex Doe ",
            Role = " Recruiter ",
            Email = "  ",
            Phone = " 0123 456 ",
            LinkedInUrl = null,
            Company = "   ",
        });

        var saved = await _contacts.GetForEditAsync(id);
        Assert.NotNull(saved);
        Assert.Equal("Alex Doe", saved.Name);
        Assert.Equal("Recruiter", saved.Role);
        Assert.Null(saved.Email);
        Assert.Equal("0123 456", saved.Phone);
        Assert.Null(saved.Company);
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("   ", null, null)]
    [InlineData("Alex", "not-an-email", null)]
    [InlineData("Alex", null, "linkedin.com/in/alex")]
    public async Task Create_rejects_invalid_input(string name, string? email, string? linkedIn)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _contacts.CreateAsync(new ContactInput { Name = name, Email = email, LinkedInUrl = linkedIn }));
    }

    [Fact]
    public async Task Create_reuses_an_existing_company_ignoring_case()
    {
        await CreateApplication(company: "Société Générale");

        await _contacts.CreateAsync(Input(company: "SOCIÉTÉ GÉNÉRALE"));

        Assert.Equal(["Société Générale"], await CompanyNames());
    }

    [Fact]
    public async Task Create_for_application_links_the_new_contact()
    {
        var appId = await CreateApplication();

        var contactId = await _contacts.CreateForApplicationAsync(appId, Input("Sam Lee", "Contoso", "Hiring manager"));

        var linked = Assert.Single(await ContactsOf(appId));
        Assert.Equal(new ContactSummary(contactId!.Value, "Sam Lee", "Hiring manager", null, null, null, "Contoso"), linked);
    }

    [Fact]
    public async Task Create_for_a_missing_application_creates_nothing()
    {
        Assert.Null(await _contacts.CreateForApplicationAsync(999, Input()));
        Assert.Empty(await _contacts.SearchAsync(null));
    }

    [Fact]
    public async Task Update_changes_fields_and_removes_a_company_nothing_else_uses()
    {
        var id = await _contacts.CreateAsync(Input(company: "Contso")); // typo

        Assert.True(await _contacts.UpdateAsync(id, Input(company: "Contoso")));

        Assert.Equal("Contoso", (await _contacts.GetForEditAsync(id))!.Company);
        Assert.Equal(["Contoso"], await CompanyNames());
    }

    [Fact]
    public async Task Update_keeps_a_company_still_used_by_an_application()
    {
        await CreateApplication(company: "Contoso");
        var id = await _contacts.CreateAsync(Input(company: "Contoso"));

        await _contacts.UpdateAsync(id, Input(company: null));

        Assert.Null((await _contacts.GetForEditAsync(id))!.Company);
        Assert.Equal(["Contoso"], await CompanyNames());
    }

    [Fact]
    public async Task Update_keeps_a_company_still_used_by_another_contact()
    {
        var first = await _contacts.CreateAsync(Input("First", company: "Fabrikam"));
        await _contacts.CreateAsync(Input("Second", company: "Fabrikam"));

        await _contacts.UpdateAsync(first, Input("First", company: "Contoso"));

        Assert.Equal(["Contoso", "Fabrikam"], await CompanyNames());
    }

    [Fact]
    public async Task Update_fixing_only_the_capitalisation_renames_a_company_only_this_contact_uses()
    {
        var id = await _contacts.CreateAsync(Input(company: "contoso"));

        await _contacts.UpdateAsync(id, Input("Renamed", company: "Contoso"));

        Assert.Equal("Contoso", (await _contacts.GetForEditAsync(id))!.Company);
        Assert.Equal(["Contoso"], await CompanyNames());
    }

    [Fact]
    public async Task Update_fixing_only_the_capitalisation_of_a_shared_company_keeps_its_name()
    {
        await CreateApplication(company: "Contoso");
        var id = await _contacts.CreateAsync(Input(company: "Contoso"));

        await _contacts.UpdateAsync(id, Input(company: "CONTOSO"));

        Assert.Equal("Contoso", (await _contacts.GetForEditAsync(id))!.Company);
        Assert.Equal(["Contoso"], await CompanyNames());
    }

    [Fact]
    public void Email_and_linkedin_are_trimmed_as_they_are_set_so_form_validation_agrees()
    {
        var input = new ContactInput { Name = "Alex", Email = " alex@example.com ", LinkedInUrl = "\thttps://example.com/in/alex " };

        Assert.Equal("alex@example.com", input.Email);
        Assert.Equal("https://example.com/in/alex", input.LinkedInUrl);
        System.ComponentModel.DataAnnotations.Validator.ValidateObject(
            input, new System.ComponentModel.DataAnnotations.ValidationContext(input), validateAllProperties: true);
    }

    [Fact]
    public async Task Update_or_delete_a_missing_contact_reports_it()
    {
        Assert.False(await _contacts.UpdateAsync(999, Input()));
        Assert.False(await _contacts.DeleteAsync(999));
        Assert.Null(await _contacts.GetForEditAsync(999));
    }

    [Fact]
    public async Task Delete_removes_the_contact_and_its_links_but_keeps_the_application()
    {
        var appId = await CreateApplication();
        var contactId = await _contacts.CreateForApplicationAsync(appId, Input(company: "Contoso"));

        Assert.True(await _contacts.DeleteAsync(contactId!.Value));

        Assert.Empty(await ContactsOf(appId));
        Assert.NotNull(await _applications.GetDetailsAsync(appId));
        Assert.Equal(["Contoso"], await CompanyNames());
    }

    [Fact]
    public async Task Delete_removes_a_company_only_that_contact_used()
    {
        var id = await _contacts.CreateAsync(Input(company: "Lonely Ltd"));

        await _contacts.DeleteAsync(id);

        Assert.Empty(await CompanyNames());
    }

    [Fact]
    public async Task Deleting_an_application_keeps_its_contacts()
    {
        var appId = await CreateApplication();
        var contactId = await _contacts.CreateForApplicationAsync(appId, Input());

        await _applications.DeleteAsync(appId);

        var item = Assert.Single(await _contacts.SearchAsync(null));
        Assert.Equal(contactId, item.Contact.Id);
        Assert.Empty(item.Applications);
    }

    [Fact]
    public async Task Link_and_unlink_an_existing_contact()
    {
        var appId = await CreateApplication();
        var contactId = await _contacts.CreateAsync(Input());

        Assert.True(await _contacts.LinkAsync(appId, contactId));
        Assert.True(await _contacts.LinkAsync(appId, contactId)); // twice is harmless
        Assert.Single(await ContactsOf(appId));

        Assert.True(await _contacts.UnlinkAsync(appId, contactId));
        Assert.Empty(await ContactsOf(appId));
        Assert.NotNull(await _contacts.GetForEditAsync(contactId)); // the contact is kept
        Assert.False(await _contacts.UnlinkAsync(appId, contactId)); // no link any more
    }

    [Fact]
    public async Task Link_with_a_missing_side_reports_it()
    {
        var appId = await CreateApplication();
        var contactId = await _contacts.CreateAsync(Input());

        Assert.False(await _contacts.LinkAsync(999, contactId));
        Assert.False(await _contacts.LinkAsync(appId, 999));
        Assert.False(await _contacts.UnlinkAsync(appId, 999));
    }

    [Fact]
    public async Task Unlink_only_removes_that_application()
    {
        var first = await CreateApplication(role: "First");
        var second = await CreateApplication(role: "Second");
        var contactId = await _contacts.CreateAsync(Input());
        await _contacts.LinkAsync(first, contactId);
        await _contacts.LinkAsync(second, contactId);

        await _contacts.UnlinkAsync(first, contactId);

        Assert.Empty(await ContactsOf(first));
        Assert.Single(await ContactsOf(second));
    }

    [Fact]
    public async Task Link_candidates_exclude_linked_ones_and_put_the_same_company_first()
    {
        var appId = await CreateApplication(company: "Contoso");
        var linked = await _contacts.CreateForApplicationAsync(appId, Input("Already Linked", "Contoso"));
        await _contacts.CreateAsync(Input("Ann Other", "Fabrikam"));
        await _contacts.CreateAsync(Input("Zoe Same", "Contoso"));
        await _contacts.CreateAsync(Input("Bob None"));

        var candidates = await _contacts.GetLinkCandidatesAsync(appId);

        Assert.Equal(["Zoe Same", "Ann Other", "Bob None"], candidates.Select(c => c.Name));
    }

    [Fact]
    public async Task Link_candidates_for_a_missing_application_are_empty()
    {
        await _contacts.CreateAsync(Input());

        Assert.Empty(await _contacts.GetLinkCandidatesAsync(999));
    }

    [Fact]
    public async Task Search_matches_name_role_email_or_company_and_lists_by_name()
    {
        await _contacts.CreateAsync(new ContactInput { Name = "Zed", Role = "Talent Partner" });
        await _contacts.CreateAsync(new ContactInput { Name = "Émile", Email = "emile@contoso.example" });
        await _contacts.CreateAsync(new ContactInput { Name = "Amy", Company = "Talentum" });
        await _contacts.CreateAsync(new ContactInput { Name = "Unrelated" });

        Assert.Equal(["Amy", "Zed"], (await _contacts.SearchAsync("TALENT")).Select(c => c.Contact.Name));
        Assert.Equal(["Émile"], (await _contacts.SearchAsync("ÉMILE")).Select(c => c.Contact.Name));
        Assert.Equal(["Émile"], (await _contacts.SearchAsync("contoso.example")).Select(c => c.Contact.Name));
        Assert.Equal(4, (await _contacts.SearchAsync("  ")).Count);
    }

    [Fact]
    public async Task Search_is_literal_for_like_wildcards()
    {
        await _contacts.CreateAsync(new ContactInput { Name = "100% Recruiter" });
        await _contacts.CreateAsync(new ContactInput { Name = "1000 Recruiter" });

        Assert.Equal(["100% Recruiter"], (await _contacts.SearchAsync("0%")).Select(c => c.Contact.Name));
    }

    [Fact]
    public async Task Search_lists_each_contacts_linked_applications()
    {
        var appId = await CreateApplication(company: "Contoso", role: "Analyst");
        await _applications.ChangeStatusAsync(appId, ApplicationStatus.Interview);
        await _contacts.CreateForApplicationAsync(appId, Input());

        var item = Assert.Single(await _contacts.SearchAsync(null));

        Assert.Equal([new LinkedApplication(appId, "Analyst", "Contoso", ApplicationStatus.Interview)], item.Applications);
    }

    [Fact]
    public async Task Details_list_linked_contacts_by_name()
    {
        var appId = await CreateApplication();
        await _contacts.CreateForApplicationAsync(appId, Input("Zoe"));
        await _contacts.CreateForApplicationAsync(appId, Input("adam"));

        Assert.Equal(["adam", "Zoe"], (await ContactsOf(appId)).Select(c => c.Name));
    }

    [Fact]
    public async Task CreateAsync_EmailAndLinkedInWithSurroundingSpaces_AreAcceptedAndStoredTrimmed()
    {
        var id = await _contacts.CreateAsync(new ContactInput
        {
            Name = "Alex Doe",
            Email = "  alex@contoso.example ",
            LinkedInUrl = "\t https://www.linkedin.com/in/alex \n",
        });

        var saved = await _contacts.GetForEditAsync(id);
        Assert.Equal("alex@contoso.example", saved!.Email);
        Assert.Equal("https://www.linkedin.com/in/alex", saved.LinkedInUrl);
    }

    [Fact]
    public async Task CreateAsync_WhitespaceOnlyLinkedInAndPhone_AreStoredAsNull()
    {
        var id = await _contacts.CreateAsync(new ContactInput { Name = "Alex", LinkedInUrl = " \t ", Phone = "  " });

        var saved = await _contacts.GetForEditAsync(id);
        Assert.Null(saved!.LinkedInUrl);
        Assert.Null(saved.Phone);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public async Task CreateAsync_NameLength_IsCheckedAfterTrimming(int length, bool accepted)
    {
        var input = new ContactInput { Name = "   " + new string('a', length) + "   " };

        if (accepted)
        {
            var id = await _contacts.CreateAsync(input);
            Assert.Equal(length, (await _contacts.GetForEditAsync(id))!.Name.Length);
        }
        else
        {
            await Assert.ThrowsAsync<ValidationException>(() => _contacts.CreateAsync(input));
        }
    }

    [Fact]
    public async Task CreateForApplicationAsync_InvalidInput_CreatesNothing()
    {
        var appId = await CreateApplication();

        await Assert.ThrowsAsync<ValidationException>(() =>
            _contacts.CreateForApplicationAsync(appId, new ContactInput { Name = "Alex", Email = "nope" }));

        Assert.Empty(await ContactsOf(appId));
        Assert.Empty(await _contacts.SearchAsync(null));
    }

    [Fact]
    public async Task UpdateAsync_InvalidInput_LeavesTheContactAndCompanyUnchanged()
    {
        var id = await _contacts.CreateAsync(Input("Alex", company: "Contoso"));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _contacts.UpdateAsync(id, new ContactInput { Name = "Renamed", Email = "not-an-email", Company = "Fabrikam" }));

        var saved = await _contacts.GetForEditAsync(id);
        Assert.Equal("Alex", saved!.Name);
        Assert.Equal("Contoso", saved.Company);
        Assert.Equal(["Contoso"], await CompanyNames());
    }

    [Fact]
    public async Task DeleteAsync_CompanyUsedByAnotherContact_IsKept()
    {
        var leaving = await _contacts.CreateAsync(Input("Leaving", company: "Fabrikam"));
        var staying = await _contacts.CreateAsync(Input("Staying", company: "Fabrikam"));

        await _contacts.DeleteAsync(leaving);

        Assert.Equal(["Fabrikam"], await CompanyNames());
        Assert.Equal("Fabrikam", (await _contacts.GetForEditAsync(staying))!.Company);
    }

    [Fact]
    public async Task DeleteAsync_LastUserOfACompanyAnApplicationMovedAwayFrom_RemovesTheCompany()
    {
        var appId = await CreateApplication(company: "Contoso");
        var contactId = await _contacts.CreateAsync(Input(company: "Contoso"));
        var form = await _applications.GetForEditAsync(appId);
        form!.Company = "Fabrikam";
        await _applications.UpdateAsync(appId, form);
        Assert.Equal(["Contoso", "Fabrikam"], await CompanyNames()); // kept for the contact

        await _contacts.DeleteAsync(contactId);

        Assert.Equal(["Fabrikam"], await CompanyNames());
    }

    [Fact]
    public async Task UpdateAsync_ContactMovesToTheApplicationsCompany_RemovesTheOldOneAndKeepsTheApplicationsCompany()
    {
        var appId = await CreateApplication(company: "Contoso");
        var contactId = await _contacts.CreateForApplicationAsync(appId, Input(company: "Contso")); // typo

        await _contacts.UpdateAsync(contactId!.Value, Input(company: "CONTOSO"));

        Assert.Equal(["Contoso"], await CompanyNames());
        Assert.Equal("Contoso", Assert.Single(await ContactsOf(appId)).CompanyName);
        Assert.Equal("Contoso", (await _applications.GetDetailsAsync(appId))!.CompanyName);
    }

    [Fact]
    public async Task GetLinkCandidatesAsync_AfterUnlink_OffersTheContactAgain()
    {
        var appId = await CreateApplication();
        var contactId = await _contacts.CreateForApplicationAsync(appId, Input("Alex"));
        Assert.Empty(await _contacts.GetLinkCandidatesAsync(appId));

        await _contacts.UnlinkAsync(appId, contactId!.Value);

        Assert.Equal([contactId.Value], (await _contacts.GetLinkCandidatesAsync(appId)).Select(c => c.Id));
    }

    [Fact]
    public async Task GetLinkCandidatesAsync_ContactLinkedToAnotherApplication_IsStillOffered()
    {
        var first = await CreateApplication(role: "First");
        var second = await CreateApplication(role: "Second");
        var contactId = await _contacts.CreateForApplicationAsync(first, Input("Alex"));

        Assert.Empty(await _contacts.GetLinkCandidatesAsync(first));
        Assert.Equal([contactId!.Value], (await _contacts.GetLinkCandidatesAsync(second)).Select(c => c.Id));
    }

    [Fact]
    public async Task GetLinkCandidatesAsync_SameCompanyTypedInAnotherCase_ComesFirstAndNamesSortIgnoringCase()
    {
        var appId = await CreateApplication(company: "Contoso");
        await _contacts.CreateAsync(Input("aaron", "Fabrikam"));
        await _contacts.CreateAsync(Input("bob", "CONTOSO"));
        await _contacts.CreateAsync(Input("Alice", "contoso"));

        var candidates = await _contacts.GetLinkCandidatesAsync(appId);

        Assert.Equal(["Alice", "bob", "aaron"], candidates.Select(c => c.Name));
        Assert.All(candidates.Take(2), c => Assert.Equal("Contoso", c.CompanyName));
    }

    [Fact]
    public async Task SearchAsync_ContactsWithoutCompany_AreFoundByNameAndSkippedByCompanySearch()
    {
        await _contacts.CreateAsync(Input("Nora Nocompany"));
        await _contacts.CreateAsync(Input("Carl Company", company: "Northwind"));

        var byName = Assert.Single(await _contacts.SearchAsync("nocompany"));
        Assert.Null(byName.Contact.CompanyName);
        Assert.Equal(["Carl Company"], (await _contacts.SearchAsync("northwind")).Select(c => c.Contact.Name));
    }

    [Fact]
    public async Task SearchAsync_TermWithSurroundingSpaces_IsTrimmed()
    {
        await _contacts.CreateAsync(Input("Alex Doe"));
        await _contacts.CreateAsync(Input("Alexis"));

        Assert.Equal(["Alex Doe"], (await _contacts.SearchAsync("  alex doe \t")).Select(c => c.Contact.Name));
    }

    [Fact]
    public async Task SearchAsync_ContactLinkedToSeveralApplications_ListsEachWithItsOwnCompany()
    {
        var first = await CreateApplication(company: "Contoso", role: "Analyst");
        var second = await CreateApplication(company: "Fabrikam", role: "Engineer");
        var contactId = await _contacts.CreateAsync(Input("Agency Recruiter", company: "Hays"));
        await _contacts.LinkAsync(second, contactId);
        await _contacts.LinkAsync(first, contactId);

        var item = Assert.Single(await _contacts.SearchAsync("agency"));

        Assert.Equal("Hays", item.Contact.CompanyName);
        Assert.Equal(
            [
                new LinkedApplication(first, "Analyst", "Contoso", ApplicationStatus.Wishlist),
                new LinkedApplication(second, "Engineer", "Fabrikam", ApplicationStatus.Wishlist),
            ],
            item.Applications);
    }
}
