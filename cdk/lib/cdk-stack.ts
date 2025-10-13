import * as cdk from "aws-cdk-lib";
import { Construct } from "constructs";
import * as lambda from "aws-cdk-lib/aws-lambda";
import * as dotnet from '@aws-cdk/aws-lambda-dotnet';
import * as tasks from 'aws-cdk-lib/aws-stepfunctions-tasks';
import * as sfn from "aws-cdk-lib/aws-stepfunctions";
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as rds from 'aws-cdk-lib/aws-rds';
import * as secretsmanager from 'aws-cdk-lib/aws-secretsmanager';

export class CdkStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props?: cdk.StackProps) {
    super(scope, id, props);

    const vpc = new ec2.Vpc(this, "TestVpc", {
      ipAddresses: ec2.IpAddresses.cidr('10.1.0.0/16'),
      maxAzs: 2,
      natGateways: 1,
    });

    const rdsSecurityGroup = new ec2.SecurityGroup(this, "RdsSecurityGroup", {
      vpc,
      description: "Allow PostgreSQL access from anywhere (for testing only)",
      allowAllOutbound: true,
    });

    rdsSecurityGroup.addIngressRule(
      ec2.Peer.anyIpv4(),
      ec2.Port.tcp(5432),
      "Allow PostgreSQL access from anywhere"
    );

        // Create a secret for DB credentials
    const dbSecret = new secretsmanager.Secret(this, 'DbSecret', {
      generateSecretString: {
        secretStringTemplate: JSON.stringify({ username: 'testuser' }),
        generateStringKey: 'password',
        excludePunctuation: true,
        includeSpace: false,
      },
    });

    // RDS instance
    const db = new rds.DatabaseInstance(this, 'TestPostgres', {
      engine: rds.DatabaseInstanceEngine.postgres({ version: rds.PostgresEngineVersion.VER_16 }),
      instanceType: ec2.InstanceType.of(ec2.InstanceClass.T4G, ec2.InstanceSize.MICRO), // cheapest instance
      vpc,
      vpcSubnets: { subnetType: ec2.SubnetType.PUBLIC },
      credentials: rds.Credentials.fromSecret(dbSecret),
      allocatedStorage: 20, // minimum
      maxAllocatedStorage: 50,
      multiAz: false, // single AZ
      securityGroups: [rdsSecurityGroup],
      publiclyAccessible: true, // for local testing
      removalPolicy: cdk.RemovalPolicy.DESTROY, // delete when stack is destroyed
      deletionProtection: false,
    });

    new cdk.CfnOutput(this, 'DBEndpoint', {
      value: db.dbInstanceEndpointAddress,
    });

    new cdk.CfnOutput(this, 'DBSecretArn', {
      value: dbSecret.secretArn,
    });
    
    const username = dbSecret.secretValueFromJson('username').unsafeUnwrap();
    const password = dbSecret.secretValueFromJson('password').unsafeUnwrap();
    const dbConnectionString = `Host=${db.dbInstanceEndpointAddress};Database=EwalletDb;Username=${username};Password=${password};Port=${db.dbInstanceEndpointPort}`;
   
    const checkSenderWalletBalanceFunction = new dotnet.DotNetFunction(this, 'CheckSenderWalletBalanceFunction', {
      projectDir: '../src/Ewallet.CheckSenderWalletBalanceFunction',
      runtime: lambda.Runtime.PROVIDED_AL2023,
      bundling: {
        msbuildParameters: ['/p:PublishAot=true'],
      },
      vpc: vpc,
      architecture: lambda.Architecture.X86_64,
      memorySize: 512,
      environment: {
        DB_CONNECTION_STRING: dbConnectionString,
      },
    });
    const checkSenderWalletBalanceFromFunctionArn = lambda.Function.fromFunctionArn(
      this,
      'CheckSenderWalletBalanceFromFunctionArn',
      checkSenderWalletBalanceFunction.functionArn
    );

    const debitSenderWalletBalanceFunction = new dotnet.DotNetFunction(this, 'DebitSenderWalletBalanceFunction', {
      projectDir: '../src/Ewallet.DebitSenderWalletBalanceFunction',
      runtime: lambda.Runtime.PROVIDED_AL2023,
      bundling: {
        msbuildParameters: ['/p:PublishAot=true'],
      },
      vpc: vpc,
      architecture: lambda.Architecture.X86_64,
      memorySize: 512,
      environment: {
        DB_CONNECTION_STRING: dbConnectionString,
      },
    });

    const debitSenderWalletBalanceFunctionArn = lambda.Function.fromFunctionArn(
      this,
      'DebitSenderWalletBalanceFunctionArn',
      debitSenderWalletBalanceFunction.functionArn
    );

    const failLambda = new dotnet.DotNetFunction(this, 'FailFunction', {
      projectDir: '../src/Fail',
      runtime: lambda.Runtime.PROVIDED_AL2023,
      bundling: {
        msbuildParameters: ['/p:PublishAot=true'],
      },
      vpc: vpc,
      architecture: lambda.Architecture.X86_64,
      memorySize: 512,
    });

    const failLambdaReference = lambda.Function.fromFunctionArn(
      this,
      'FailLambda',
      failLambda.functionArn
    );

    const checkSenderWalletBalanceTask = new tasks.LambdaInvoke(this, 'Check Sender Wallet Balance Task', {
      lambdaFunction: checkSenderWalletBalanceFromFunctionArn,
      outputPath: '$.Payload',
    });

    const debitSenderWalletBalanceTask = new tasks.LambdaInvoke(this, 'Debit Sender Wallet Balance Task', {
      lambdaFunction: debitSenderWalletBalanceFunctionArn,
      outputPath: '$.Payload',
    });

    const failTask = new tasks.LambdaInvoke(this, 'Run Fail Lambda', {
      lambdaFunction: failLambdaReference,
      outputPath: '$.Payload',
    });

    const definition = checkSenderWalletBalanceTask
      .addCatch(failTask, { resultPath: "$.error" })
      .next(debitSenderWalletBalanceTask.addCatch(failTask, { resultPath: "$.error" }));

    new sfn.StateMachine(this, "EwalletTransactionSagaOrchestration", {
      definitionBody: sfn.DefinitionBody.fromChainable(definition),
      timeout: cdk.Duration.minutes(5),
    });
  }
}
